using LoveNestLauncher.Common;

namespace LoveNestLauncher.Models;

public sealed class NavItem : ObservableObject
{
    private bool _isSelected;

    public required int Index { get; init; }
    public required string Title { get; init; }
    public required string Glyph { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
