using DevHub.Integration;
using DevHub.Services;
using DevHub.ViewModels.Git;

namespace DevHub.ViewModels.Documents;

/// <summary>
/// The app services a repository tab (and its diff tabs) use, in one DI-registered bundle so
/// the factories pass one thing instead of a growing parameter list.
/// </summary>
/// <param name="Editor">Opens files in the editor for the repository's language (or the Windows default without Projects).</param>
/// <param name="Dialogs">Confirmations (discard).</param>
/// <param name="Status">Status bar messages.</param>
/// <param name="Terminal">Opens a terminal in the repository (Open Terminal).</param>
/// <param name="Host">The hosting service (GitHub: Publish, your avatar), or null when no hosting plugin is on.</param>
/// <param name="AddFiles">Templates' Add Files from Template, or null when that plugin is off.</param>
public sealed record RepositoryTabServices(RepositoryFileOpener Editor, IDialogService Dialogs, IStatusService Status,
    TerminalLauncher Terminal, IRemoteHost? Host = null, IAddFilesFromTemplate? AddFiles = null);
