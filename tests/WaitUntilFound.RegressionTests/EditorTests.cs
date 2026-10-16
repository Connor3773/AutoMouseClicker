using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Ming_AutoClicker.Models;
using Ming_AutoClicker.Services;
using Ming_AutoClicker.ViewModels;
using Ming_AutoClicker.Views;

internal static partial class Program
{
    private static int RunEditorTests(Application application)
    {
        // Load the real editor without starting App, registering hotkeys, or displaying a window.
        foreach (var file in new[] { "Colors", "TextStyles", "Buttons", "Inputs", "Lists", "ScrollBars", "Tabs", "Windows" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"/Ming-AutoClicker;component/Styles/{file}.xaml", UriKind.Relative)
            });

        var failures = 0;
        foreach (var language in new[] { AppLanguage.English, AppLanguage.SimplifiedChinese })
        {
            try
            {
                LocalizationService.Current.ApplyLanguage(language);
                VerifyEditorBindings();
                Console.WriteLine($"PASS Editor bindings, input bounds and per-step settings ({language})");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL Editor ({language}): {ex}");
            }
        }
        LocalizationService.Current.ApplyLanguage(AppLanguage.English);
        Console.WriteLine($"{2 - failures}/2 editor tests passed.");
        return failures;
    }

    private static void VerifyEditorBindings()
    {
        using var capture = new ScreenCaptureService();
        using var matcher = new FakeMatcher(capture, _ => MatchResult.NotFound);
        var profile = CreateProfile(waitUntilFound: true);
        var first = (FindImageAction)profile.Actions[0];
        var second = (FindImageAction)CreateSkipProfile(37).Actions[0];
        profile.Actions.Add(second);
        using var editor = new MacroEditorViewModel(new MacroStorageService(), capture, matcher, profile);
        editor.SelectedAction = first;
        var view = new MacroEditorView { DataContext = editor };
        view.Measure(new Size(1100, 900));
        view.Arrange(new Rect(0, 0, 1100, 900));
        view.UpdateLayout();
        var skip = FindBoundControl<CheckBox>(view, CheckBox.IsCheckedProperty, nameof(editor.SkipIfNotFound));
        var timeout = FindBoundControl<TextBox>(view, TextBox.TextProperty, nameof(editor.SkipTimeoutSeconds));
        Require(!skip.IsEnabled && !timeout.IsEnabled, "Infinite waiting must disable skip controls.");
        Require((string)skip.Content == LocalizationService.Current.GetString("EditorSkipIfNotFound"),
            "The checkbox must use the active language.");

        editor.SelectedAction = second;
        Require(skip.IsEnabled && timeout.IsEnabled && skip.IsChecked == true && timeout.Text == "37",
            "Selecting another step must refresh all skip controls.");
        SetTimeoutText(timeout, "300");
        Require(second.SkipTimeoutSeconds == 300, "The upper boundary must be editable.");
        SetTimeoutText(timeout, "301");
        Require(second.SkipTimeoutSeconds == 300 && timeout.Text == "300", "Values above 300 must display the clamped value.");
        SetTimeoutText(timeout, "-1");
        Require(second.SkipTimeoutSeconds == 0 && timeout.Text == "0", "Negative values must clamp to zero.");
        SetTimeoutText(timeout, "invalid");
        Require(second.SkipTimeoutSeconds == 0 && timeout.Text == "0", "Invalid text must restore the last valid value.");
        SetTimeoutText(timeout, "12");
        Require(second.SkipTimeoutSeconds == 12, "A custom number of seconds must update the action.");

        editor.WaitUntilFound = true;
        Require(!skip.IsEnabled && !timeout.IsEnabled, "Enabling infinite waiting must disable timeout editing.");
        editor.WaitUntilFound = false;
        Require(skip.IsEnabled && timeout.IsEnabled && second.SkipTimeoutSeconds == 12,
            "Disabling infinite waiting must restore the previous skip settings.");
        skip.SetCurrentValue(CheckBox.IsCheckedProperty, false);
        skip.GetBindingExpression(CheckBox.IsCheckedProperty)!.UpdateSource();
        Require(!second.SkipIfNotFound && !timeout.IsEnabled, "Unchecking skip must disable its timeout.");
        Require(first.WaitUntilFound && !first.SkipIfNotFound && first.SkipTimeoutSeconds == 0,
            "Editing the second step must not change the first step.");
        editor.SelectedAction = first;
        Require(!skip.IsEnabled && !timeout.IsEnabled && timeout.Text == "0", "Returning to the first step must show its own settings.");
        view.DataContext = null;
    }

    private static void SetTimeoutText(TextBox box, string text)
    {
        box.SetCurrentValue(TextBox.TextProperty, text);
        box.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
        box.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, box));
    }

    private static T FindBoundControl<T>(DependencyObject root, DependencyProperty property, string path)
        where T : FrameworkElement
    {
        var matches = Descendants(root).OfType<T>().Where(control =>
            BindingOperations.GetBinding(control, property)?.Path.Path == path);
        return matches.Single();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child))
                yield return descendant;
    }
}
