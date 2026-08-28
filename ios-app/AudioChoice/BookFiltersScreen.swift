import SwiftUI

/// Per-book filter controls, matching the Android client's Playback Filters screen.
///
/// Deliberately offers no way to jump to a flagged moment. An earlier version of this
/// screen listed every event with a "Go to event" link, which let anyone browse
/// straight to the content the filters exist to remove, and it was not behind the
/// parental PIN either.
struct BookFiltersScreen: View {
    let record: LibraryBookRecord

    @State private var pinIsSet = ParentalPinStore.isSet
    @State private var settings: BookFilterSettings = .everythingFiltered
    @State private var expandedCategory: String?
    @State private var expandedGroup: String?
    @State private var unlocked = false
    @State private var showingPinPrompt = false
    @State private var enteredPin = ""
    @State private var pinError: String?
    @State private var showingSaveProfile = false
    @State private var profileName = ""

    private var suggestedProfileName: String {
        let off = settings.disabledCategoryIDs.count + settings.disabledGroupIDs.count
        return "My filters (\(off) off)"
    }

    private func saveProfile() {
        let name = profileName.trimmingCharacters(in: .whitespacesAndNewlines)
        profileName = ""
        guard !name.isEmpty else { return }
        let saved = settings
        Task {
            await FilterProfileStore.shared.save(name: name, settings: saved, makeActive: true)
        }
    }

    private var hierarchy: [PlaybackFilterCategory] {
        PlaybackFilterTaxonomy.available(record.scanResult?.events ?? [])
    }
    private var availability: FilterAvailability { FilterAvailability.of(record) }
    /// Locked until the PIN is entered, for this visit only.
    ///
    /// Android leaves the switches visible but permanently disabled while parental
    /// controls are on. Here they can be unlocked, because Parental Controls and the FAQ
    /// both tell the listener they will be asked for the PIN before changes are made, and
    /// offering no way to enter it would make the app's own description wrong.
    private var isLocked: Bool { pinIsSet && !unlocked }

    var body: some View {
        ScrollView {
            VStack(spacing: 16) {
                ACCard {
                    Text(summary)
                        .font(.footnote)
                        .foregroundStyle(availability == .unavailable ? .red : ACTheme.secondaryText)
                        .fixedSize(horizontal: false, vertical: true)
                }

                if isLocked {
                    ACCard {
                        HStack {
                            Label("Filters are locked", systemImage: "lock.fill")
                                .foregroundStyle(ACTheme.accent)
                            Spacer()
                            Button("Enter PIN") {
                                pinError = nil
                                showingPinPrompt = true
                            }
                            .foregroundStyle(ACTheme.accent)
                        }
                    }
                }

                if let pinError {
                    ACCard {
                        Text(pinError).font(.footnote).foregroundStyle(.red)
                    }
                }

                if hierarchy.isEmpty {
                    ACCard {
                        Text(emptyMessage)
                            .font(.footnote)
                            .foregroundStyle(ACTheme.secondaryText)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                } else {
                    ACCard {
                        VStack(alignment: .leading, spacing: 0) {
                            ForEach(hierarchy) { category in
                                categoryRow(category)
                                if expandedCategory == category.id {
                                    ForEach(category.groups) { group in
                                        groupRow(group)
                                        if expandedGroup == group.id {
                                            ForEach(group.controls) { control in
                                                controlRow(control)
                                            }
                                        }
                                    }
                                }
                                if category.id != hierarchy.last?.id { Divider() }
                            }
                        }
                    }
                }
            }
            .padding()
        }
        .background(ACTheme.background)
        .navigationTitle("Playback Filters")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            // Only offered once there is something to save. A profile of "filters
            // everything" is what every book already does.
            if settings.hasExceptions && !isLocked {
                Button("Save as Profile", systemImage: "square.and.arrow.down") {
                    profileName = suggestedProfileName
                    showingSaveProfile = true
                }
            }
        }
        .alert("Save as profile", isPresented: $showingSaveProfile) {
            TextField("Profile name", text: $profileName)
            Button("Save") { saveProfile() }
            Button("Cancel", role: .cancel) { profileName = "" }
        } message: {
            Text("Saves the categories and groups you switched off, and starts new books that way. This book keeps its own settings, and so does every other book you have already adjusted.")
        }
        .task {
            pinIsSet = ParentalPinStore.isSet
            settings = await BookFilterSettingsStore.refresh(
                bookID: record.id,
                accountLibraryID: record.accountLibraryID
            )
        }
        .alert("Enter parental PIN", isPresented: $showingPinPrompt) {
            SecureField("PIN", text: $enteredPin).keyboardType(.numberPad)
            Button("Unlock") {
                if ParentalPinStore.verify(enteredPin) {
                    unlocked = true
                    pinError = nil
                } else {
                    pinError = "That PIN did not match. Filters are still locked."
                }
                enteredPin = ""
            }
            Button("Cancel", role: .cancel) { enteredPin = "" }
        } message: {
            Text("Enter the PIN to change this book's filters.")
        }
    }

    private var summary: String {
        switch availability {
        case .unavailable:
            return "This audiobook's filter data could not be loaded, so nothing is being filtered right now."
        case .loading:
            return "Loading this audiobook's filters…"
        case .available:
            if isLocked {
                return "Parental Controls are on. Filter choices are visible but locked until the PIN is entered."
            }
            return "Only filters detected in this audiobook are shown. Everything starts on; changes apply only to this book."
        }
    }

    private var emptyMessage: String {
        switch availability {
        case .loading: return "Loading this audiobook's filters…"
        case .unavailable:
            return "No filter data is available for this audiobook yet. It may still be scanning, "
                + "or AudioChoice could not reach the scan service. Playback is not filtered until it loads."
        case .available: return "No filterable content was detected in this audiobook."
        }
    }

    private func categoryRow(_ category: PlaybackFilterCategory) -> some View {
        HStack(spacing: 10) {
            Image(systemName: category.icon)
                .foregroundStyle(ACTheme.accent)
                .frame(width: 24)
            Button {
                expandedCategory = expandedCategory == category.id ? nil : category.id
            } label: {
                HStack(spacing: 8) {
                    Image(systemName: expandedCategory == category.id ? "chevron.down" : "chevron.right")
                        .font(.caption)
                        .foregroundStyle(ACTheme.secondaryText)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(category.label)
                        Text("\(category.controlCount) filter controls")
                            .font(.caption)
                            .foregroundStyle(ACTheme.secondaryText)
                    }
                }
            }
            .buttonStyle(.plain)
            .accessibilityLabel("\(category.label), \(category.controlCount) filter controls")
            .accessibilityHint(expandedCategory == category.id ? "Collapse" : "Expand")
            Spacer()
            switchFor(
                label: category.label,
                isOn: isCategoryEnabled(category),
                action: { setCategory(category, enabled: $0) }
            )
        }
        .padding(.vertical, 8)
    }

    private func groupRow(_ group: PlaybackFilterGroup) -> some View {
        HStack(spacing: 8) {
            Button {
                expandedGroup = expandedGroup == group.id ? nil : group.id
            } label: {
                HStack(spacing: 8) {
                    Image(systemName: expandedGroup == group.id ? "chevron.down" : "chevron.right")
                        .font(.caption2)
                        .foregroundStyle(ACTheme.secondaryText)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(group.label).font(.subheadline)
                        Text("\(group.controls.count) controls")
                            .font(.caption2)
                            .foregroundStyle(ACTheme.secondaryText)
                    }
                }
            }
            .buttonStyle(.plain)
            .accessibilityLabel("\(group.label), \(group.controls.count) controls")
            Spacer()
            switchFor(
                label: group.label,
                isOn: isGroupEnabled(group),
                action: { setGroup(group, enabled: $0) }
            )
        }
        .padding(.leading, 34)
        .padding(.vertical, 6)
    }

    private func controlRow(_ control: PlaybackFilterControl) -> some View {
        HStack(spacing: 8) {
            VStack(alignment: .leading, spacing: 2) {
                Text(control.label)
                    .font(.caption)
                    
                    .fixedSize(horizontal: false, vertical: true)
                // A repeated word shows how often it occurs. A single moment shows when
                // it happens, so a listener can tell two similar entries apart -- but
                // there is deliberately no control here that plays it.
                if control.isAggregate {
                    Text("\(control.occurrences) occurrences")
                        .font(.caption2)
                        .foregroundStyle(ACTheme.secondaryText)
                } else if let startTime = control.startTime {
                    Text(timestamp(startTime))
                        .font(.caption2.monospacedDigit())
                        .foregroundStyle(ACTheme.secondaryText)
                }
            }
            Spacer()
            switchFor(
                label: control.label,
                isOn: isControlEnabled(control),
                action: { setControl(control, enabled: $0) }
            )
        }
        .padding(.leading, 60)
        .padding(.vertical, 4)
    }

    /// A bare switch announces only "on" or "off", which says nothing about what it
    /// controls once VoiceOver has moved past the label.
    private func switchFor(
        label: String,
        isOn: Bool,
        action: @escaping (Bool) -> Void
    ) -> some View {
        Toggle("", isOn: Binding(get: { isOn }, set: action))
            .labelsHidden()
            .disabled(isLocked)
            .accessibilityLabel("Filter \(label)")
            .accessibilityValue(isOn ? "Filtering" : "Not filtering")
    }

    private func timestamp(_ seconds: Double) -> String {
        let total = max(Int(seconds), 0)
        return String(format: "%d:%02d:%02d", total / 3600, (total % 3600) / 60, total % 60)
    }

    // MARK: - Reading and writing choices

    private func isCategoryEnabled(_ category: PlaybackFilterCategory) -> Bool {
        !settings.disabledCategoryIDs.contains(category.id)
    }

    private func isGroupEnabled(_ group: PlaybackFilterGroup) -> Bool {
        !settings.disabledGroupIDs.contains(group.id)
            && !settings.disabledCategoryIDs.contains(group.categoryID)
    }

    private func isControlEnabled(_ control: PlaybackFilterControl) -> Bool {
        control.isAggregate
            ? !settings.disabledAggregateKeys.contains(control.key)
            : !settings.disabledEventKeys.contains(control.key)
    }

    private func setCategory(_ category: PlaybackFilterCategory, enabled: Bool) {
        commit(BookFilterEditor.setCategory(
            category.id, enabled: enabled, in: settings, hierarchy: hierarchy
        ))
    }

    private func setGroup(_ group: PlaybackFilterGroup, enabled: Bool) {
        commit(BookFilterEditor.setGroup(
            group.id, enabled: enabled, in: settings, hierarchy: hierarchy
        ))
    }

    private func setControl(_ control: PlaybackFilterControl, enabled: Bool) {
        commit(BookFilterEditor.setControl(
            control, enabled: enabled, in: settings, hierarchy: hierarchy
        ))
    }

    private func commit(_ updated: BookFilterSettings) {
        settings = updated
        BookFilterSettingsStore.update(
            updated,
            bookID: record.id,
            accountLibraryID: record.accountLibraryID
        )
        // Playback holds its own copy so the skip planner is not reading UserDefaults on
        // every tick; without this a change would not take effect until the book reopened.
        NotificationCenter.default.post(
            name: .bookFilterSettingsDidChange,
            object: nil,
            userInfo: ["bookID": record.id]
        )
    }
}

extension Notification.Name {
    static let bookFilterSettingsDidChange = Notification.Name("bookFilterSettingsDidChange")
}
