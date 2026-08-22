import Combine
import Foundation

final class FilterManager: ObservableObject {

    @Published var profile: FilterProfile {
        didSet {
            onProfileChanged?(profile)
        }
    }

    var onProfileChanged: ((FilterProfile) -> Void)?

    init(profile: FilterProfile = FilterProfile()) {
        self.profile = profile
    }

    // MARK: - Category

    func setCategory(
        _ categoryID: UUID,
        enabled: Bool
    ) {
        guard let index = profile.categories.firstIndex(
            where: { $0.id == categoryID }
        ) else {
            return
        }

        profile.categories[index]
            .setEnabled(enabled)
    }

    // MARK: - Group

    func setGroup(
        _ groupID: UUID,
        enabled: Bool
    ) {
        for categoryIndex in profile.categories.indices {

            guard let groupIndex =
                profile.categories[categoryIndex]
                    .groups
                    .firstIndex(where: { $0.id == groupID })
            else {
                continue
            }

            profile.categories[categoryIndex]
                .groups[groupIndex]
                .setEnabled(enabled)

            profile.categories[categoryIndex]
                .refreshEnabledState()

            return
        }
    }

    // MARK: - Event

    func setEvent(
        _ eventID: UUID,
        enabled: Bool
    ) {
        for categoryIndex in profile.categories.indices {

            for groupIndex in profile.categories[categoryIndex]
                .groups.indices {

                guard let eventIndex =
                    profile.categories[categoryIndex]
                        .groups[groupIndex]
                        .events
                        .firstIndex(where: { $0.id == eventID })
                else {
                    continue
                }

                profile.categories[categoryIndex]
                    .groups[groupIndex]
                    .events[eventIndex]
                    .isEnabled = enabled

                profile.categories[categoryIndex]
                    .groups[groupIndex]
                    .refreshEnabledState()

                profile.categories[categoryIndex]
                    .refreshEnabledState()

                return
            }
        }
    }
}
