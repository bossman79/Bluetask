using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using System;

namespace Bluetask.Controls
{
    public sealed partial class SidebarNavigation : UserControl
    {
        public event EventHandler<NavigationEventArgs>? NavigationRequested;

        public string ActiveItem { get; private set; } = "Dashboard";

        public SidebarNavigation()
        {
            this.InitializeComponent();
            SetActiveItem("Dashboard");
        }

        private void NavItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                string itemName = button.Content?.ToString() ?? string.Empty;
                if (!string.IsNullOrEmpty(itemName))
                {
                    SetActiveItem(itemName);
                    NavigationRequested?.Invoke(this, new NavigationEventArgs(itemName));
                }
            }
        }

        public void SetActiveItem(string itemName)
        {
            // Reset all buttons to inactive state
            SetButtonActive(DashboardBtn, false);
            SetButtonActive(ProcessesBtn, false);
            SetButtonActive(PerformanceBtn, false);
            SetButtonActive(SettingsBtn, false);

            // Set the clicked button as active
            var target = GetButtonByName(itemName);
            if (target != null)
            {
                SetButtonActive(target, true);
                ActiveItem = itemName;
            }
        }

        private Button? GetButtonByName(string itemName)
        {
            switch ((itemName ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "dashboard": return DashboardBtn;
                case "processes": return ProcessesBtn;
                case "performance": return PerformanceBtn;
                case "settings": return SettingsBtn;
                default: return null;
            }
        }

        private static void SetButtonActive(Button button, bool isActive)
        {
            if (isActive)
            {
                VisualStateManager.GoToState(button, "Active", true);
            }
            else
            {
                VisualStateManager.GoToState(button, "Inactive", true);
            }
        }

        private void SidebarHover_Entered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            // Expand the shell and reveal text on each item
            var sbExpand = (Storyboard)Resources["ExpandSidebarStoryboard"];
            sbExpand.Begin();

            VisualStateManager.GoToState(DashboardBtn, "Expanded", true);
            VisualStateManager.GoToState(ProcessesBtn, "Expanded", true);
            VisualStateManager.GoToState(PerformanceBtn, "Expanded", true);
            VisualStateManager.GoToState(SettingsBtn, "Expanded", true);
        }

        private void SidebarHover_Exited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            // Collapse the shell and hide text on each item
            var sbCollapse = (Storyboard)Resources["CollapseSidebarStoryboard"];
            sbCollapse.Begin();

            VisualStateManager.GoToState(DashboardBtn, "Collapsed", true);
            VisualStateManager.GoToState(ProcessesBtn, "Collapsed", true);
            VisualStateManager.GoToState(PerformanceBtn, "Collapsed", true);
            VisualStateManager.GoToState(SettingsBtn, "Collapsed", true);
        }
    }

    public sealed class NavigationEventArgs : EventArgs
    {
        public string NavigationTarget { get; }
        public NavigationEventArgs(string target)
        {
            NavigationTarget = target;
        }
    }
}



