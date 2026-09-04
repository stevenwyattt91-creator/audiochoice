import StoreKit
import SwiftUI

/// The subscription paywall.
///
/// Shown from Profile at any time -- not gated behind hitting a limit -- because the premium
/// voice itself is what is gated (see `NarrationTierStore` on Android; iOS narration gating is
/// not yet built), and a listener deciding whether to subscribe should be able to find this
/// screen without first bumping into a wall.
struct PremiumScreen: View {
    @ObservedObject private var purchases = PurchaseManager.shared
    @State private var errorMessage: String?

    var body: some View {
        Form {
            Section {
                VStack(alignment: .leading, spacing: 10) {
                    HStack(spacing: 0) {
                        Text("Audio").font(.title2.bold())
                        Text("Choice").font(.title2.bold()).foregroundStyle(ACTheme.accent)
                        Text(" Premium").font(.title2.bold())
                    }
                    Text("The most natural narration voice, closest to a human narrator.")
                        .foregroundStyle(ACTheme.secondaryText)
                }
                .padding(.vertical, 6)
            }
            .listRowBackground(Color.clear)

            if purchases.access.plan == "premium" && purchases.access.isActive {
                Section {
                    Label("You're subscribed to AudioChoice Premium.", systemImage: "checkmark.seal.fill")
                        .foregroundStyle(ACTheme.accent)
                    if let expiresAt = purchases.access.expiresAt {
                        Text("Renews \(expiresAt.formatted(date: .abbreviated, time: .omitted)).")
                            .font(.footnote)
                            .foregroundStyle(ACTheme.secondaryText)
                    }
                }
            } else if purchases.access.plan == "founder" {
                Section {
                    Label("You have free lifetime Founder access.", systemImage: "star.fill")
                        .foregroundStyle(ACTheme.accent)
                }
            } else if let product = purchases.products.first {
                Section {
                    Button {
                        Task { await buy(product) }
                    } label: {
                        HStack {
                            Text("Subscribe — \(product.displayPrice)/month")
                            Spacer()
                            if purchases.isPurchasing { ProgressView() }
                        }
                    }
                    .disabled(purchases.isPurchasing)
                }
                Section {
                    Button("Restore Purchases") { Task { await purchases.restorePurchases() } }
                        .disabled(purchases.isPurchasing)
                }
            } else if purchases.isLoadingProducts {
                Section { ProgressView("Checking availability…") }
            } else {
                Section {
                    Text("AudioChoice Premium is not available for purchase yet. Please check back soon.")
                        .foregroundStyle(ACTheme.secondaryText)
                }
                Section {
                    Button("Restore Purchases") { Task { await purchases.restorePurchases() } }
                        .disabled(purchases.isPurchasing)
                }
            }

            if let errorMessage {
                Section { Text(errorMessage).foregroundStyle(.orange) }
            }
        }
        .navigationTitle("Premium")
        .navigationBarTitleDisplayMode(.inline)
        .acScreen()
        .task {
            await purchases.loadProducts()
            await purchases.refreshAccess()
        }
    }

    private func buy(_ product: Product) async {
        errorMessage = nil
        do {
            try await purchases.purchase(product)
        } catch PurchaseError.userCancelled {
            // Not an error worth showing.
        } catch {
            errorMessage = error.localizedDescription
        }
    }
}
