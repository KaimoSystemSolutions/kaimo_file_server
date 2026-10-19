using Kaimo_File_Server.Web.Services;
using Xunit;

namespace Kaimo_File_Server.Tests;

public class FileSelectionCoordinatorTests
{
    [Fact]
    public void RequestSelection_IsHandledImmediatelyByMatchingOpenFolder()
    {
        var coordinator = new FileSelectionCoordinator();
        string? selectedItem = null;
        coordinator.SelectionRequested += () =>
        {
            if (coordinator.TryConsume("documents", "invoices/2026", out var itemName))
                selectedItem = itemName;
        };

        var handled = coordinator.RequestSelection(
            "documents", "invoices/2026", "invoice-42.pdf");

        Assert.True(handled);
        Assert.Equal("invoice-42.pdf", selectedItem);
    }

    [Fact]
    public void RequestSelection_RemainsPendingUntilDestinationFolderLoads()
    {
        var coordinator = new FileSelectionCoordinator();
        coordinator.SelectionRequested += () =>
            coordinator.TryConsume("documents", "currently-open", out _);

        var handled = coordinator.RequestSelection(
            "documents", @"invoices\2026", "invoice-42.pdf");

        Assert.False(handled);
        Assert.True(coordinator.TryConsume(
            "documents", "/invoices/2026/", out var selectedItem));
        Assert.Equal("invoice-42.pdf", selectedItem);
    }
}
