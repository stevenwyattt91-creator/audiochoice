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

    /// Whether this grant has not yet run out, judged only by its own expiry.
    ///
    /// Used when the server cannot be reached, so the entitlement's own end date is what decides
    /// rather than an arbitrary offline grace period. A founder or comp grant carries no expiry and
    /// is treated as open-ended, which is how the server reads it too.
    var isUnexpired: Bool {
        guard let expiresAt else { return true }
        return expiresAt > Date()
    }
}

/// This device's copy of the last access answer the server gave.
///
/// Exists because `PurchaseManager.access` used to begin every launch at `.free` and was only ever
/// replaced by a successful network call. With the API unreachable the refresh failed silently, the
/// value stayed `.free`, and `PaywallGate` -- which stands in for the whole app -- concluded the
/// account had no subscription and showed the paywall. A paying listener was locked out of
/// audiobooks already imported into private storage on their own phone, which need no server to
/// play, and was asked to buy a subscription they already had.
///
/// A network failure is not evidence about an entitlement. What the server last said is.
private enum AccessCache {
    private static let key = "cachedAccountAccess"

    static func save(_ access: AccountAccessResponse) {
        guard let data = try? JSONEncoder().encode(access) else { return }
        UserDefaults.standard.set(data, forKey: key)
    }

    static func load() -> AccountAccessResponse? {
        guard let data = UserDefaults.standard.data(forKey: key) else { return nil }
        return try? JSONDecoder().decode(AccountAccessResponse.self, from: data)
    }

    /// Cleared on sign-out, so the next account to use this device does not inherit the previous
    /// one's access while the server is unreachable.
    static func clear() {
        UserDefaults.standard.removeObject(forKey: key)
    }
}

/// The subscription product this build offers.
///
/// Matches the Product ID actually created in App Store Connect exactly, as it must -- StoreKit
/// resolves products by this identifier alone, and it cannot be edited in App Store Connect once
/// saved, so this is the side that has to agree with it.
enum StoreProducts {
    static let premiumMonthly = "Monthly"
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
        // Starts from what the server last said rather than from `.free`, so a launch that cannot
        // reach the server does not begin by assuming the account has nothing.
        if let cached = AccessCache.load(), cached.isUnexpired { access = cached }
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
    ///
    /// The server is the authority and its answer always wins, including a revocation. But when it
    /// cannot be reached, access falls back rather than collapsing to `.free`: first to whatever
    /// subscription StoreKit can vouch for on this device, then to the last answer the server gave.
    /// Neither fallback outlives its own expiry date.
    ///
    /// Silently answering "no subscription" to an unreachable server is what took the whole app away
    /// from paying listeners during an API outage, playback of already-imported books included.
    func refreshAccess() async {
        if let client = try? CloudScanClient.configured(),
           let fetched = try? await client.accountAccess() {
            access = fetched
            AccessCache.save(fetched)
            return
        }

        // StoreKit before the cache: a receipt on this device is current evidence of a subscription
        // that Apple keeps up to date, where the cache is only a record of an older conversation.
        if let local = await subscribedAccessFromStoreKit() {
            access = local
            return
        }

        if let cached = AccessCache.load(), cached.isUnexpired { access = cached }
    }

    /// Access derived from Apple's own record of this device's subscription, for when the server is
    /// unreachable.
    ///
    /// Covers the paid subscription only. A founder or comp grant exists solely on the server and
    /// StoreKit has never heard of it, which is why `AccessCache` remains the second fallback.
    private func subscribedAccessFromStoreKit() async -> AccountAccessResponse? {
        for await result in Transaction.currentEntitlements {
            guard let transaction = try? Self.checkVerified(result),
                  transaction.productID == StoreProducts.premiumMonthly,
                  transaction.revocationDate == nil else { continue }
            if let expiry = transaction.expirationDate, expiry <= Date() { continue }
            return AccountAccessResponse(
                isActive: true,
                plan: "premium",
                source: "apple",
                expiresAt: transaction.expirationDate,
                canUseFilters: true,
                canUseCompanion: true)
        }
        return nil
    }

    /// Drops this device's record of account access, called when the session is cleared so the next
    /// account to sign in here does not inherit the previous one's.
    func forgetAccess() {
        access = .free
        AccessCache.clear()
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
        AccessCache.save(access)
    }

    private static func checkVerified<T>(_ result: VerificationResult<T>) throws -> T {
        switch result {
        case .unverified: throw PurchaseError.verificationFailed
        case let .verified(value): return value
        }
    }
}
