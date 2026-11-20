using System.Windows;

namespace UiAtlas.Presentation;

/// <summary>A page that places its content gutter inside its viewport so the scrollbar can reach the window edge.</summary>
public interface IWorkspaceViewport
{
    const string RightGutter = "WorkspaceViewportRightGutter";

    static void Inset(FrameworkElement content) =>
        content.SetResourceReference(FrameworkElement.MarginProperty, RightGutter);
}
