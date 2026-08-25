import SwiftUI

struct FilterProfileScreen: View {
    @AppStorage("filterEnabled.profanity") private var profanity = true
    @AppStorage("filterEnabled.sexualContent") private var sexualContent = true
    @AppStorage("filterEnabled.graphicViolence") private var graphicViolence = false
    @AppStorage("filterEnabled.drugsAndAlcohol") private var drugsAndAlcohol = false
    @AppStorage("filterEnabled.blasphemy") private var blasphemy = false
    @AppStorage("filterEnabled.selfHarm") private var selfHarm = false
    @AppStorage("parentalPin") private var parentalPin = ""
    @State private var enteredPin = ""
    @State private var unlocked = false
    @State private var showPinPrompt = false

    var body: some View {
        ScrollView {
            VStack(spacing: 20) {
                if !parentalPin.isEmpty && !unlocked {
                    ACCard {
                        HStack {
                            Label("Filters are locked", systemImage: "lock.fill").foregroundStyle(ACTheme.accent)
                            Spacer()
                            Button("Unlock") { showPinPrompt = true }.foregroundStyle(ACTheme.accent)
                        }
                    }
                }
                ACCard {
                    Text("Content Categories").font(.title3.bold())
                    Text("Enabled categories are always skipped during playback.")
                        .font(.caption)
                        .foregroundStyle(ACTheme.secondaryText)
                        .padding(.bottom, 8)
                    filterRow(.profanity, isOn: $profanity)
                    filterRow(.sexualContent, isOn: $sexualContent)
                    filterRow(.graphicViolence, isOn: $graphicViolence)
                    filterRow(.drugsAndAlcohol, isOn: $drugsAndAlcohol)
                    filterRow(.blasphemy, isOn: $blasphemy)
                    filterRow(.selfHarm, isOn: $selfHarm)
                }
                .disabled(!parentalPin.isEmpty && !unlocked)

                ACCard {
                    Label {
                        Text("Custom-word filtering requires word-level scan events. It will be enabled after the backend taxonomy supports them.")
                    } icon: {
                        Image(systemName: "text.magnifyingglass")
                            .foregroundStyle(ACTheme.accent)
                    }
                    .font(.footnote)
                    .foregroundStyle(ACTheme.secondaryText)
                }
                .disabled(!parentalPin.isEmpty && !unlocked)
            }
            .padding()
        }
        .background(ACTheme.background)
        .navigationTitle("Filters")
        .overlay(alignment: .top) {
            if !parentalPin.isEmpty && !unlocked {
                Button { showPinPrompt = true } label: { Text("Filters are locked — enter PIN to make changes").font(.footnote.weight(.semibold)).padding(10).background(ACTheme.panel).clipShape(Capsule()) }
                    .padding(.top, 8)
            }
        }
        .alert("Enter parental PIN", isPresented: $showPinPrompt) {
            SecureField("PIN", text: $enteredPin)
                .keyboardType(.numberPad)
            Button("Unlock") { if enteredPin == parentalPin { unlocked = true }; enteredPin = "" }
            Button("Cancel", role: .cancel) { enteredPin = "" }
        } message: { Text("Enter the 4–6 digit PIN to change filters.") }
    }

    private func filterRow(_ category: FilterCategory, isOn: Binding<Bool>) -> some View {
        HStack(spacing: 12) {
            Image(systemName: category.icon)
                .foregroundStyle(ACTheme.accent)
                .frame(width: 24)
            VStack(alignment: .leading) {
                Text(category.title)
                Text("Detected scan events")
                    .font(.caption)
                    .foregroundStyle(ACTheme.secondaryText)
            }
            Spacer()
            Toggle("", isOn: isOn).labelsHidden()
        }
        .padding(.vertical, 8)
    }
}
