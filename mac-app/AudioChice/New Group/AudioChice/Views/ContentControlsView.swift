import SwiftUI

struct ContentControlsView: View {

    @ObservedObject var filterManager: FilterManager

    var body: some View {

        NavigationStack {

            List {

                ForEach(
                    $filterManager.profile.categories
                ) { $category in

                    Section {

                        DisclosureGroup {

                            ForEach(
                                $category.groups
                            ) { $group in

                                DisclosureGroup {

                                    ForEach(
                                        $group.events
                                    ) { $event in

                                        Toggle(
                                            event.title,
                                            isOn: Binding(

                                                get: {
                                                    event.isEnabled
                                                },

                                                set: { enabled in

                                                    filterManager.setEvent(
                                                        event.id,
                                                        enabled: enabled
                                                    )
                                                }
                                            )
                                        )
                                    }

                                } label: {

                                    Toggle(
                                        "\(group.displayName) (\(group.eventCount))",
                                        isOn: Binding(

                                            get: {
                                                group.isEnabled
                                            },

                                            set: { enabled in

                                                filterManager.setGroup(
                                                    group.id,
                                                    enabled: enabled
                                                )
                                            }
                                        )
                                    )
                                }
                            }

                        } label: {

                            Toggle(
                                "\(category.displayName) (\(category.eventCount))",
                                isOn: Binding(

                                    get: {
                                        category.isEnabled
                                    },

                                    set: { enabled in

                                        filterManager.setCategory(
                                            category.id,
                                            enabled: enabled
                                        )
                                    }
                                )
                            )
                        }

                    }
                }
            }
            .navigationTitle("Content Controls")
        }
    }
}
