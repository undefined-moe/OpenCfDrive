using OpenCfDrive.Core.Configuration;

namespace OpenCfDrive.Tests;

/// <summary>
/// When the app may point the drive at another folder or remote: never where
/// the folder holds another remote's files, which would be deleted locally or
/// uploaded to the wrong place.
/// </summary>
public sealed class DriveChangeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "opencfdrive-change", Guid.NewGuid().ToString("N"));
    private readonly HashSet<string> _syncRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DriveState> _states = new(StringComparer.OrdinalIgnoreCase);

    private string Current => Path.Combine(_folder, "drive");

    public DriveChangeTests()
    {
        // The current drive: registered, and tracking box:'s files.
        Directory.CreateDirectory(Current);
        _syncRoots.Add(Current);
        _states[Current] = new DriveState("box:", 12);
    }

    private DriveChange Check(string newRoot, string newRemote) =>
        DriveChange.Check(Current, "box:", newRoot, newRemote, _syncRoots.Contains, root => _states.GetValueOrDefault(root));

    [Fact]
    public void NothingChanges_IsAllowed_AndTouchesNothing()
    {
        var change = Check(Current + "\\", "box");

        Assert.True(change.Allowed);
        Assert.False(change.ChangesDrive);
    }

    [Fact]
    public void AnotherRemote_InTheSameFolder_IsRefused()
    {
        var change = Check(Current, "dropbox:");

        Assert.False(change.Allowed);
        Assert.Contains("new, empty drive folder", change.Problem);
    }

    [Fact]
    public void ANewAddedDrive_WithoutACurrentRemote_CallsItANewDrive_NotARemoteChange()
    {
        var folder = Path.Combine(_folder, "second");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "something.txt"), "in the way");

        var change = DriveChange.Check(folder, null, folder, "dropbox:", _syncRoots.Contains, root => _states.GetValueOrDefault(root));

        Assert.False(change.Allowed);
        Assert.Contains("not empty", change.Problem);
        Assert.DoesNotContain("another remote", change.Problem);
    }

    [Fact]
    public void AnotherRemote_InTheSameFolder_IsAllowed_WhenTheDriveTracksNothing()
    {
        _states[Current] = new DriveState("e2e:", 0);

        var change = Check(Current, "dropbox:");

        Assert.True(change.Allowed);
        Assert.Null(change.OldSyncRootToUnregister);
        Assert.True(change.DiscardOldState);
    }

    [Fact]
    public void ANewFolder_MovesTheDrive_AndUnregistersTheOldOne()
    {
        var target = Path.Combine(_folder, "new");

        var change = Check(target, "box:");

        Assert.True(change.Allowed);
        Assert.Equal(Current, change.OldSyncRootToUnregister);
        Assert.Equal(target, change.NewSyncRoot);
        Assert.False(change.DiscardOldState);
    }

    [Fact]
    public void AnEmptyFolder_MayTakeAnotherRemote()
    {
        var target = Path.Combine(_folder, "empty");
        Directory.CreateDirectory(target);

        var change = Check(target, "dropbox:");

        Assert.True(change.Allowed);
        Assert.Equal(Current, change.OldSyncRootToUnregister);
    }

    [Fact]
    public void AFolderWithFiles_IsRefused()
    {
        var target = Path.Combine(_folder, "full");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "mine.txt"), "x");

        var change = Check(target, "box:");

        Assert.False(change.Allowed);
        Assert.Contains("not empty", change.Problem);
    }

    [Fact]
    public void TheFormerDriveOfThisRemote_MayBeUsedAgain()
    {
        var target = Path.Combine(_folder, "former");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "downloaded.txt"), "x");
        _states[target] = new DriveState("box:", 3);

        var change = Check(target, "box:");

        Assert.True(change.Allowed);
        Assert.False(change.DiscardOldState);
    }

    [Fact]
    public void TheFormerDriveOfAnotherRemote_IsRefused()
    {
        var target = Path.Combine(_folder, "former");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "downloaded.txt"), "x");
        _states[target] = new DriveState("dropbox:", 3);

        Assert.False(Check(target, "box:").Allowed);
    }

    [Fact]
    public void AnEmptyFolderWithOldState_DiscardsThatState()
    {
        var target = Path.Combine(_folder, "emptied");
        Directory.CreateDirectory(target);
        _states[target] = new DriveState("dropbox:", 3);

        var change = Check(target, "box:");

        Assert.True(change.Allowed);
        Assert.True(change.DiscardOldState);
    }

    [Fact]
    public void ARegisteredFolder_IsNeverListed()
    {
        // Another drive that tracks files: refused from its state alone,
        // without listing it (that would ask its provider to fill it).
        var other = Path.Combine(_folder, "other-drive");
        Directory.CreateDirectory(other);
        _syncRoots.Add(other);
        _states[other] = new DriveState("dropbox:", 5);

        Assert.False(Check(other, "box:").Allowed);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
