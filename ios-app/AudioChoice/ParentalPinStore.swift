import Foundation
import Security
import CryptoKit

/// The parental PIN, kept out of UserDefaults.
///
/// It previously lived in plain `@AppStorage`, which put the credential guarding the
/// content filters into the app's preferences plist: readable from a file-system backup
/// or anything with access to the container, and included in unencrypted device backups.
///
/// Two changes. The value is held in the Keychain rather than preferences, and what is
/// held is a salted SHA-256 of the PIN rather than the PIN itself, so nothing that can be
/// typed back in is stored anywhere.
///
/// The honest limit: a 4-to-6 digit PIN is a million candidates at most, so anyone who
/// extracts the hash can recover it by exhaustion. Hashing removes the casual read;
/// keeping it device-only and behind first unlock is what actually protects it. This is a
/// control for sharing a household device, not a defence against a determined attacker
/// with the unlocked hardware.
enum ParentalPinStore {
    private static let service = "com.audiochoice.mobile.parental"
    private static let account = "filter-pin"
    private static let legacyDefaultsKey = "parentalPin"

    private struct Verifier: Codable {
        let salt: Data
        let hash: Data
    }

    static var isSet: Bool { loadVerifier() != nil }

    static func set(_ pin: String) {
        var salt = Data(count: 16)
        let status = salt.withUnsafeMutableBytes { buffer in
            SecRandomCopyBytes(kSecRandomDefault, 16, buffer.baseAddress!)
        }
        // Without a random salt the stored value would be a plain digest of a short PIN,
        // which is the same for every install and therefore a lookup rather than a search.
        guard status == errSecSuccess else { return }
        save(Verifier(salt: salt, hash: digest(pin: pin, salt: salt)))
    }

    static func verify(_ pin: String) -> Bool {
        guard let verifier = loadVerifier() else { return false }
        let candidate = digest(pin: pin, salt: verifier.salt)
        // Constant-time comparison, so how long the check takes says nothing about how
        // much of the digest matched.
        guard candidate.count == verifier.hash.count else { return false }
        var difference: UInt8 = 0
        for (left, right) in zip(candidate, verifier.hash) { difference |= left ^ right }
        return difference == 0
    }

    static func clear() {
        SecItemDelete(identity() as CFDictionary)
        UserDefaults.standard.removeObject(forKey: legacyDefaultsKey)
    }

    /// Moves a PIN written by an earlier build into the Keychain and deletes the original.
    ///
    /// Without this the old plaintext value would sit in preferences indefinitely, and
    /// anyone who had set a PIN would find the lock silently gone after updating.
    static func migrateLegacyPinIfNeeded() {
        let defaults = UserDefaults.standard
        guard let legacy = defaults.string(forKey: legacyDefaultsKey), !legacy.isEmpty else {
            return
        }
        if !isSet { set(legacy) }
        defaults.removeObject(forKey: legacyDefaultsKey)
    }

    static func isValidPin(_ value: String) -> Bool {
        value.count >= 4 && value.count <= 6 && value.allSatisfy(\.isNumber)
    }

    private static func digest(pin: String, salt: Data) -> Data {
        var input = salt
        input.append(Data(pin.utf8))
        return Data(SHA256.hash(data: input))
    }

    private static func identity() -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account
        ]
    }

    private static func save(_ verifier: Verifier) {
        SecItemDelete(identity() as CFDictionary)
        guard let data = try? JSONEncoder().encode(verifier) else { return }
        var item = identity()
        item[kSecValueData as String] = data
        // Device-only: a parental lock set on one device should not travel to another
        // through a Keychain sync or an encrypted backup restored elsewhere.
        item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        SecItemAdd(item as CFDictionary, nil)
    }

    private static func loadVerifier() -> Verifier? {
        var query = identity()
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data else { return nil }
        return try? JSONDecoder().decode(Verifier.self, from: data)
    }
}
