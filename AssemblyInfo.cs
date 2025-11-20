using System.Windows;  // Brings in WPF types like ThemeInfo and ResourceDictionaryLocation attributes

[assembly:ThemeInfo(                      // Declares WPF theme/resource location metadata at the assembly level
    ResourceDictionaryLocation.None,      // First argument: there are no separate theme-specific resource dictionaries
                                          // (so WPF will not look for resources in theme-specific locations)
    ResourceDictionaryLocation.SourceAssembly   // Second argument: generic resources live in this assembly's compiled resources
                                               // (so WPF looks in the application's own resource dictionaries for defaults)
)]                                        // Closes the ThemeInfo attribute specification
