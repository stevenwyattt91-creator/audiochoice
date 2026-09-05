import Foundation
import StoreKit

/// Account-level access, mirroring `AccountAccessResponse` on the server exactly (see
/// `backend/AudioChoice.Api/Contracts/EntitlementContracts.cs`).
struct AccountAccessResponse: Codable {
    let isActive: Bool
    let plan: String
    let source: String
    let expiresAt: Date?
    let canUseFilters: Bool
    let canUseCompanion: Bool

    static let free = AccountAccessResponse(
        isActive: false, plan: "free", source: "none", expiresAt: nil,
        canUseFilters: false, canUseCompanion: false)
}

/// The subscription product this build offers.
///
/// A single placeholder id until App Store Connect has a real product configured -- StoreKit
/// simply reports zero products for an id that does not exist yet, which `PurchaseManager` treats
/// as "not available" rather than as an error, so this file needs no change once the real id
/// exists; only this constant does.
enum StoreProducts {
    static let premiumMonthly = "com.audiochoice.mobile.premium.monthly"
}

enum PurchaseError: LocalizedError {
    case notAvailable
    case verificationFailed
    case serverRejected(String)
    case userCancelled

    var errorDescription: String? {
        switch self {
        case .notAvailable: "AudioChoice Premium is not available for purchase yet. Please check back soon."
        case .verificationFailed: "Apple could not verify this purchase. Please try again."
        case let .serverRejected(message): message
        case .userCancelled: nil // Not an error worth showing; the listener chose to cancel.
        }
    }
}

/// Drives StoreKit2 purchases and keeps this device's copy of account access current.
///
/// `Transaction.updates` is observed for the app's whole lifetime rather than only during an
/// active purchase, because a subscription can also start, renew, or be refunded on a different
/// device (Family Sharing, a second phone) or while this app is not in the foreground, and
/// StoreKit delivers all of those the same way -- as a transaction this listener would otherwise
/// miss entirely.
@MainActor
final class PurchaseManager: ObservableObject {
    static let shared = PurchaseManager()

    @Published private(set) var products: [Product] = []
    @Published private(set) var access: AccountAccessResponse = .free
    @Published private(set) var isLoadingProducts = false
    @Published private(set) var isPurchasing = false

    private var updatesTask: Task<Void, Never>?

    private init() {
        updatesTask = Task { [weak self] in await self?.observeTransactionUpdates() }
    }

    deinit {
        updatesTask?.cancel()
    }

    /// Loads the storefront's current price/details for `StoreProducts.premiumMonthly`.
    ///
    /// An empty result is not surfaced as an error: it is exactly what StoreKit returns for a
    /// product id that does not exist in App Store Connect yet, which is the expected state
    /// until the subscription is configured there.
    func loadProducts() async {
        isLoadingProducts = true
        defer { isLoadingProducts = false }
        products = (try? await Product.products(for: [StoreProducts.premiumMonthly])) ?? []
    }

    /// Starts the standard StoreKit2 purchase sheet for a product from `loadProducts()`.
    func purchase(_ product: Product) async throws {
        isPurchasing = true
        defer { isPurchasing = false }

        let result = try await product.purchase()
        switch result {
        case let .success(verification):
            let transaction = try Self.checkVerified(verification)
            try await submit(jws: verification.jwsRepresentation)
            await transaction.finish()
        case .userCancelled:
            throw PurchaseError.userCancelled
        case .pending:
            // Ask to Buy or similar. Nothing to submit yet -- Transaction.updates delivers the
            // real transaction later if and when it clears.
            break
        @unknown default:
            break
        }
    }

    /// Re-checks whichever subscription is already on this Apple ID, for a "Restore Purchases"
    /// button -- StoreKit does not require a network purchase to learn this.
    func restorePurchases() async {
        for await result in Transaction.currentEntitlements {
            guard let transaction = try? Self.checkVerified(result),
                  transaction.productID == StoreProducts.premiumMonthly else { continue }
            try? await submit(jws: result.jwsRepresentation)
        }
    }

    /// Asks the server for this account's current access, independent of any local purchase --
    /// covers sign-in on a device that never ran a purchase itself.
    func refreshAccess() async {
        guard let client = try? CloudScanClient.configured() else { return }
        if let fetched = try? await client.accountAccess() { access = fetched }
    }

    private func observeTransactionUpdates() async {
        for await result in Transaction.updates {
            guard let transaction = try? Self.checkVerified(result) else { continue }
            try? await submit(jws: result.jwsRepresentation)
            await transaction.finish()
        }
    }

    /// Sends the transaction's own signed JWS to the server, which re-derives the product and
    /// expiry from Apple's payload rather than trusting anything decoded here -- this client-side
    /// check exists only to decide whether to bother submitting at all.
    ///
    /// `jwsRepresentation` is read from the `VerificationResult` itself, not from the unwrapped
    /// `Transaction` -- the signed form is a property of the envelope StoreKit verified, not of
    /// the payload inside it.
    private func submit(jws: String) async throws {
        guard let client = try? CloudScanClient.configured() else {
            throw PurchaseError.serverRejected("Sign in to AudioChoice before subscribing.")
        }
        access = try await client.submitAppleTransaction(signedTransactionInfo: jws)
    }

    private static func checkVerified<T>(_ result: VerificationResult<T>) throws -> T {
        switch result {
        case .unverified: throw PurchaseError.verificationFailed
        case let .verified(value): return value
        }
    }
}
