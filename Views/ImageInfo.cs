namespace Photon.Views;

// One photo in the current folder.
public sealed record ImageInfo(string Path, int Index)
{
    public string DisplayName => System.IO.Path.GetFileName(Path);
}
