import StoreKit
import SwiftUI

/// The only plans that let an account past `PaywallGate`.
///
/// Deliberately narrower than "any active grant": the backend's entitlement store marks a row
/// active whenever it exists and has not expired, regardless of what plan name it carries, so a
/// future plan added for some other purpose (a trial, a comp, a refund-pending state) would pass
/// `isActive` without belonging on either list here. Named explicitly instead, so admitting a new
/// plan through the gate is a one-line decision made on purpose, not an accident of a plan simply
/// existing.
private let gatedAccessPlans: Set<String> = ["founder", "premium"]

/// Decides whether a signed-in account may reach the app at all.
///
/// At this launch, AudioChoice has no free tier beyond creating an account: audiobook playback,
/// content filtering, and the EPUB reader all sit behind the subscription. `PurchaseManager.access`
/// is therefore checked here, not inside `RootTabView` or any individual screen -- gating it once,
/// at the root, means no tab, no deep link, and no future screen can be added without remembering to
/// re-check access itself.
struct PaywallGate: View {
    @ObservedObject private var purchases = PurchaseManager.shared
    @State private var hasCheckedAccess = false

    /// Whether the four-page intro guide has been seen, held here rather than in `AudioChoiceApp`
    /// so the tour runs on the far side of the gate.
    ///
    /// Every page of it points at something the subscription pays for, so it belongs after access
    /// is granted, not before: shown earlier it described features the account could not open, and
    /// it delayed the one screen a new listener actually needs to act on.
    ///
    /// Still device-wide rather than per-account, which is deliberate -- it records that this
    /// person has seen the tour on this phone, and `AuthSession.signOut()` leaves it alone so
    /// signing back in does not replay it.
    @AppStorage("onboardingCompleted") private var onboardingCompleted = false

    private var hasAccess: Bool {
        purchases.access.isActive && gatedAccessPlans.contains(purchases.access.plan)
    }

    var body: some View {
        Group {
            if !hasCheckedAccess {
                ZStack {
                    ACTheme.background.ignoresSafeArea()
                    ProgressView().tint(ACTheme.accent)
                }
            } else if hasAccess {
                if onboardingCompleted {
                    RootTabView()
                } else {
                    OnboardingScreen(completed: $onboardingCompleted)
                }
            } else {
                NavigationStack {
                    PaywallScreen()
                }
            }
        }
        .task {
            await purchases.refreshAccess()
            hasCheckedAccess = true
        }
    }
}

/// The subscribe-or-go-no-further screen shown in place of the app for an account with no active
/// entitlement.
///
/// Distinct from `PremiumScreen`: that one is reached voluntarily from Profile and assumes the
/// listener is already using the app for free. This one is the app's front door when there is
/// nothing free to use -- it has to explain what AudioChoice does (nobody has seen it yet), and it
/// has to offer a way out (Sign Out) for someone who signed into the wrong account or changed
/// their mind, since there is no Profile tab to reach otherwise.
///
/// For the same reason it also has to offer Delete Account. `PaywallGate` replaces the whole app
/// for an account without an entitlement, so `RootTabView` -- and with it Profile and the delete
/// control on `AccountScreen` -- is unreachable from here. That leaves the one state an App Review
/// reviewer is guaranteed to be in, a fresh account with nothing purchased, as the one state with
/// no way to delete the account, which is what Review Guideline 5.1.1(v) requires. Signing out is
/// not a substitute: the account still exists afterwards.
private struct PaywallScreen: View {
    @ObservedObject private var purchases = PurchaseManager.shared
    @ObservedObject private var session = AuthSession.shared
    @State private var errorMessage: String?
    @State private var confirmingSignOut = false
    @State private var confirmingDelete = false
    @State private var deletingAccount = false
    @State private var redeemingCode = false

    var body: some View {
        ScrollView {
            VStack(spacing: 28) {
                Spacer().frame(height: 12)
                Image(systemName: "headphones")
                    .font(.system(size: 54, weight: .light))
                    .foregroundStyle(ACTheme.accent)
                VStack(spacing: 6) {
                    HStack(spacing: 0) {
                        Text("Audio").font(.largeTitle.bold())
                        Text("Choice").font(.largeTitle.bold()).foregroundStyle(ACTheme.accent)
                    }
                    Text("Listen Your Way").foregroundStyle(ACTheme.secondaryText)
                }

                ACCard {
                    VStack(alignment: .leading, spacing: 16) {
                        featureRow("checkmark.shield", "Filter sensitive content by category, not all-or-nothing")
                        featureRow("book", "Attach an EPUB to follow along word for word as you listen")
                        featureRow("lock", "Protect your filter choices with a parental PIN")
                        featureRow("iphone", "Your audio stays in private storage on this device")
                    }
                }

                if let product = purchases.products.first {
                    VStack(spacing: 10) {
                        Button {
                            Task { await subscribe(product) }
                        } label: {
                            HStack {
                                Text("Subscribe — \(product.displayPrice)/month")
                                Spacer()
                                if purchases.isPurchasing { ProgressView() }
                            }
                            .frame(maxWidth: .infinity)
                            .padding()
                        }
                        .buttonStyle(.borderedProminent)
                        .tint(ACTheme.accent)
                        .foregroundStyle(.black)
                        .disabled(purchases.isPurchasing)

                        secondaryActions
                    }
                } else if purchases.isLoadingProducts {
                    ProgressView("Checking availability…")
                } else {
                    VStack(spacing: 10) {
                        Text("Subscriptions are not loading right now. You can still redeem a "
                            + "code, or restore a subscription you already have.")
                            .multilineTextAlignment(.center)
                            .foregroundStyle(ACTheme.secondaryText)
                        secondaryActions
                    }
                }

                if let errorMessage {
                    Text(errorMessage).foregroundStyle(.orange).multilineTextAlignment(.center)
                }

                VStack(spacing: 4) {
                    Button("Sign Out", role: .destructive) { confirmingSignOut = true }
                    Button(deletingAccount ? "Deleting…" : "Delete Account", role: .destructive) {
                        confirmingDelete = true
                    }
                    .disabled(deletingAccount)
                    .font(.footnote)
                }
                .padding(.top, 8)

                Spacer().frame(height: 12)
            }
            .padding(.horizontal, 28)
        }
        .background(ACTheme.background.ignoresSafeArea())
        .navigationBarTitleDisplayMode(.inline)
        .confirmationDialog(
            "Sign out of AudioChoice?",
            isPresented: $confirmingSignOut,
            titleVisibility: .visible
        ) {
            Button("Sign Out", role: .destructive) { session.signOut() }
            Button("Cancel", role: .cancel) {}
        }
        .confirmationDialog(
            "Delete your AudioChoice account?",
            isPresented: $confirmingDelete,
            titleVisibility: .visible
        ) {
            Button("Delete Account", role: .destructive) { Task { await deleteAccount() } }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This permanently deletes your library, filter choices, and account. This cannot be undone. If you have an active subscription, cancel it separately in your Apple ID subscription settings.")
        }
        .task { await purchases.loadProducts() }
        // Apple's own redemption sheet rather than a text field of ours: an offer code is checked
        // by the App Store, and redeeming one *is* the purchase, so there is nothing for this app
        // to validate or charge. A redemption started here comes back through the same
        // `Transaction.updates` listener as any other purchase, which submits it to the server and
        // republishes `access` -- so the gate opens on its own, with no navigation from here.
        .offerCodeRedemption(isPresented: $redeemingCode) { result in
            switch result {
            case .success:
                // Reports only that the sheet closed without error, not that a code was accepted,
                // so the entitlement still has to be re-read before concluding anything. Harmless
                // when nothing was redeemed: both calls are the same ones the buttons make.
                Task {
                    await purchases.restorePurchases()
                    await purchases.refreshAccess()
                }
            case let .failure(error):
                errorMessage = error.localizedDescription
            }
        }
    }

    /// Redeem and Restore, shown whether or not StoreKit returned a product.
    ///
    /// Both belong in the no-product branch too. Redemption is Apple's sheet and needs nothing from
    /// `purchases.products`, and someone who already subscribed still has to be able to get back in
    /// on a new phone -- so a storefront that answers slowly, or not at all, must not leave this
    /// screen with nothing to tap. That state is reachable in practice: it is what a newly approved
    /// subscription looks like for the first while after review, before it finishes propagating.
    @ViewBuilder private var secondaryActions: some View {
        Button("Redeem a Code") { redeemingCode = true }
            .disabled(purchases.isPurchasing)
        Button("Restore Purchases") { Task { await restore() } }
            .disabled(purchases.isPurchasing)
    }

    private func featureRow(_ icon: String, _ text: String) -> some View {
        HStack(alignment: .top, spacing: 14) {
            Image(systemName: icon).foregroundStyle(ACTheme.accent).frame(width: 22)
            Text(text).foregroundStyle(ACTheme.secondaryText)
        }
    }

    private func subscribe(_ product: Product) async {
        errorMessage = nil
        do {
            try await purchases.purchase(product)
        } catch PurchaseError.userCancelled {
            // Not an error worth showing.
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    private func restore() async {
        errorMessage = nil
        await purchases.restorePurchases()
    }

    /// Deletes the account and, on success, leaves this screen because `AuthSession.deleteAccount`
    /// clears the local session, which sends the app back to sign-in.
    ///
    /// A failure deliberately keeps the listener here with the reason shown rather than signing them
    /// out, matching `AuthSession.deleteAccount`: an account that could not be deleted still exists,
    /// and clearing the session would claim otherwise. The backend answers 409 with an explanation
    /// for an account carrying audit work, and that text is what surfaces here.
    private func deleteAccount() async {
        errorMessage = nil
        deletingAccount = true
        defer { deletingAccount = false }
        do {
            try await session.deleteAccount()
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}
