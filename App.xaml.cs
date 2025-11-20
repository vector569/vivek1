﻿using System.Configuration;           // Provides access to app configuration (App.config), unused currently but common in WPF templates
using System.Data;                    // Data-related types (DataSet, etc.), not used right now
using System.Windows;                 // Core WPF types including Application, Window, etc.

namespace SttRecorderApp;             // Declares the namespace for this project; matches XAML root namespace

/// <summary>
/// Interaction logic for App.xaml  // Standard auto-generated XML doc comment, refers to this being the code-behind for App.xaml
/// </summary>
public partial class App : Application // Partial class that completes the App definition started in App.xaml; inherits from WPF Application
{
}                                      // Empty body; all startup behavior is currently handled by XAML (StartupUri) and base Application
