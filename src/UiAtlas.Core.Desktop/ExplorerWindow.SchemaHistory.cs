using System.Windows;
using System.Windows.Controls;
using UiAtlas.Core.Contracts;
using UiAtlas.Core.Mapping.Atlas;
using UIMaps.Protocol.V3;

namespace UiAtlas.Core.Desktop;

public sealed partial class ExplorerWindow
{
    private Button SchemaHistoryButton(ParameterProposal proposal)
    {
        var button = new Button { Content = "Schema history…", Margin = new Thickness(0, 5, 0, 5), Padding = new Thickness(8, 4, 8, 4) };
        button.Click += async (_, _) =>
        {
            if (_graphPath is null || !AtlasMapWorkspace.Exists(_graphPath)) return;
            try
            {
                var workspace = AtlasMapWorkspace.Open(_graphPath);
                var draft = workspace.Drafts.Single(d => d.Entities.Any(e => e.MapEntityRef == proposal.TargetId));
                var grid = draft.Entities.Single(e => e.MapEntityRef == proposal.TargetId && e.HostedDataGrid is not null);
                var window = new GridSchemaHistoryWindow(draft, grid, value => Task.Run(() => workspace.Review(new ReviewCommand
                {
                    RequestId = Guid.NewGuid().ToString("N"), MapDraftRef = draft.MapDraftRef, ExpectedEtag = draft.Etag,
                    CommandKind = ReviewCommandKind.ReviewSelectGridSchema, SubjectRef = grid.MapEntityRef,
                    Value = value, ReviewedBy = "local-user:schema-history", Reason = "User selected the grid schema default",
                }))) { Owner = this };
                if (window.ShowDialog() == true) await LoadGraphAsync(_graphPath);
            }
            catch (Exception error) { MessageBox.Show(this, error.Message, "Schema history", MessageBoxButton.OK, MessageBoxImage.Information); }
        };
        return button;
    }
}
