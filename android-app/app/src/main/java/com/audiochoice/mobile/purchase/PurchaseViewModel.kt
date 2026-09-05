package com.audiochoice.mobile.purchase

import android.app.Activity
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import com.android.billingclient.api.BillingClient
import com.android.billingclient.api.BillingClient.BillingResponseCode
import com.android.billingclient.api.BillingClient.ProductType
import com.android.billingclient.api.BillingClientStateListener
import com.android.billingclient.api.BillingFlowParams
import com.android.billingclient.api.BillingResult
import com.android.billingclient.api.Purchase
import com.android.billingclient.api.PurchasesUpdatedListener
import com.android.billingclient.api.ProductDetails
import com.android.billingclient.api.QueryProductDetailsParams
import com.android.billingclient.api.QueryPurchasesParams
import com.android.billingclient.api.acknowledgePurchase
import com.android.billingclient.api.queryProductDetails
import com.android.billingclient.api.queryPurchasesAsync
import com.audiochoice.contracts.AccountAccessResponse
import com.audiochoice.contracts.GooglePurchaseRequest
import com.audiochoice.mobile.data.AudioChoiceApi
import com.audiochoice.mobile.data.SessionStore
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch

/**
 * The subscription product this build offers.
 *
 * A single placeholder id until Play Console has a real product configured. Play Billing
 * simply returns no [ProductDetails] for an id that does not exist yet, which this view model
 * treats as "not available" rather than as an error, so this file needs no change once the
 * real id exists; only this constant does.
 */
object StoreProducts {
    const val PREMIUM_MONTHLY = "com.audiochoice.mobile.premium.monthly"
}

data class PurchaseUiState(
    val access: AccountAccessResponse = AccountAccessResponse(),
    val product: ProductDetails? = null,
    val offerToken: String? = null,
    val isLoadingProducts: Boolean = true,
    val isPurchasing: Boolean = false,
    val error: String? = null,
)

/**
 * Drives Play Billing purchases and keeps this device's copy of account access current.
 *
 * A single [BillingClient] connection is kept open for the view model's lifetime rather than
 * opened per call, matching Google's own guidance: a purchase completed while this app is
 * merely running (Family Sharing approval, a purchase on another device) is delivered through
 * [PurchasesUpdatedListener] only while a connection is live, and would otherwise be missed
 * until the next explicit [queryPurchasesAsync] call.
 */
class PurchaseViewModel(
    private val api: AudioChoiceApi,
    private val sessions: SessionStore,
    activity: Activity,
) : ViewModel() {
    private val mutableState = MutableStateFlow(PurchaseUiState())
    val state: StateFlow<PurchaseUiState> = mutableState.asStateFlow()

    private val purchasesUpdatedListener = PurchasesUpdatedListener { result, purchases ->
        when (result.responseCode) {
            BillingResponseCode.OK -> purchases?.forEach { handlePurchase(it) }
            BillingResponseCode.USER_CANCELED -> mutableState.value = mutableState.value.copy(isPurchasing = false)
            else -> mutableState.value = mutableState.value.copy(
                isPurchasing = false,
                error = "Google Play reported an error (${result.responseCode}). Please try again.",
            )
        }
    }

    private val billingClient = BillingClient.newBuilder(activity)
        .setListener(purchasesUpdatedListener)
        .enablePendingPurchases(
            com.android.billingclient.api.PendingPurchasesParams.newBuilder().enableOneTimeProducts().build(),
        )
        .enableAutoServiceReconnection()
        .build()

    init {
        billingClient.startConnection(object : BillingClientStateListener {
            override fun onBillingSetupFinished(billingResult: BillingResult) {
                if (billingResult.responseCode == BillingResponseCode.OK) {
                    viewModelScope.launch {
                        loadProduct()
                        restorePendingPurchases()
                    }
                } else {
                    mutableState.value = mutableState.value.copy(isLoadingProducts = false)
                }
            }
            override fun onBillingServiceDisconnected() {
                // enableAutoServiceReconnection() handles retry; nothing to do here.
            }
        })
        refreshAccess()
    }

    override fun onCleared() {
        billingClient.endConnection()
        super.onCleared()
    }

    /** Launches the standard Play Billing purchase sheet for the loaded subscription. */
    fun purchase(activity: Activity) {
        val product = mutableState.value.product ?: return
        val offerToken = mutableState.value.offerToken ?: return
        mutableState.value = mutableState.value.copy(isPurchasing = true, error = null)

        val params = BillingFlowParams.newBuilder()
            .setProductDetailsParamsList(
                listOf(
                    BillingFlowParams.ProductDetailsParams.newBuilder()
                        .setProductDetails(product)
                        .setOfferToken(offerToken)
                        .build(),
                ),
            )
            .build()
        val result = billingClient.launchBillingFlow(activity, params)
        if (result.responseCode != BillingResponseCode.OK) {
            mutableState.value = mutableState.value.copy(
                isPurchasing = false,
                error = "Google Play could not open the purchase screen (${result.responseCode}).",
            )
        }
        // Otherwise: onPurchasesUpdated (registered above) delivers the result.
    }

    /** Re-checks purchases already on this account, for a "Restore Purchases" button. */
    fun restorePurchases() {
        viewModelScope.launch { restorePendingPurchases() }
    }

    fun dismissError() {
        mutableState.value = mutableState.value.copy(error = null)
    }

    private suspend fun loadProduct() {
        val params = QueryProductDetailsParams.newBuilder()
            .setProductList(
                listOf(
                    QueryProductDetailsParams.Product.newBuilder()
                        .setProductId(StoreProducts.PREMIUM_MONTHLY)
                        .setProductType(ProductType.SUBS)
                        .build(),
                ),
            )
            .build()
        val result = runCatching { billingClient.queryProductDetails(params) }.getOrNull()
        val product = result?.productDetailsList?.firstOrNull()
        val offerToken = product?.subscriptionOfferDetails?.firstOrNull()?.offerToken
        mutableState.value = mutableState.value.copy(
            product = product,
            offerToken = offerToken,
            isLoadingProducts = false,
        )
    }

    private suspend fun restorePendingPurchases() {
        val params = QueryPurchasesParams.newBuilder().setProductType(ProductType.SUBS).build()
        val result = runCatching { billingClient.queryPurchasesAsync(params) }.getOrNull()
        result?.purchasesList?.forEach { handlePurchase(it) }
    }

    private fun handlePurchase(purchase: Purchase) {
        if (purchase.purchaseState != Purchase.PurchaseState.PURCHASED) return
        if (!purchase.products.contains(StoreProducts.PREMIUM_MONTHLY)) return

        viewModelScope.launch {
            val session = sessions.session.first() ?: run {
                mutableState.value = mutableState.value.copy(
                    isPurchasing = false,
                    error = "Sign in to AudioChoice before subscribing.",
                )
                return@launch
            }
            try {
                val access = api.submitGooglePurchase(
                    session.accessToken,
                    GooglePurchaseRequest(StoreProducts.PREMIUM_MONTHLY, purchase.purchaseToken),
                )
                mutableState.value = mutableState.value.copy(access = access, isPurchasing = false)

                // Client-side acknowledgment as a fallback: the server's Acknowledge call
                // (via GooglePlayClient) already covers the 3-day requirement in the normal
                // case, but this keeps the purchase safe even if that request failed silently
                // or purchase verification was not yet configured server-side.
                if (!purchase.isAcknowledged) {
                    val acknowledgeParams = com.android.billingclient.api.AcknowledgePurchaseParams
                        .newBuilder()
                        .setPurchaseToken(purchase.purchaseToken)
                        .build()
                    runCatching { billingClient.acknowledgePurchase(acknowledgeParams) }
                }
            } catch (error: Exception) {
                mutableState.value = mutableState.value.copy(
                    isPurchasing = false,
                    error = error.message ?: "That purchase could not be verified.",
                )
            }
        }
    }

    private fun refreshAccess() {
        viewModelScope.launch {
            val session = sessions.session.first() ?: return@launch
            val access = runCatching { api.accountAccess(session.accessToken) }.getOrNull() ?: return@launch
            mutableState.value = mutableState.value.copy(access = access)
        }
    }

    class Factory(
        private val api: AudioChoiceApi,
        private val sessions: SessionStore,
        private val activity: Activity,
    ) : ViewModelProvider.Factory {
        @Suppress("UNCHECKED_CAST")
        override fun <T : ViewModel> create(modelClass: Class<T>): T =
            PurchaseViewModel(api, sessions, activity) as T
    }
}
