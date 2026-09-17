namespace DotNotes.Core.Notes;

/// <summary>
/// Whether a <see cref="NoteTreeEntry"/> represents a markdown note file
/// or a folder inside the vault.
/// </summary>
public enum NoteEntryType
{
    File,
    Folder
}
