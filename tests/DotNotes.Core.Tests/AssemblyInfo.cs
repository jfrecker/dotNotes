using Xunit;

// Some tests here (VaultWatcherServiceTests) drive a real
// System.IO.FileSystemWatcher end-to-end. Observed in practice: running
// this test class's collection concurrently with an unrelated collection
// (xUnit's default behaviour - each class is its own collection, and
// collections run in parallel by default) can cause a watcher's
// FileSystemWatcher to miss its file-creation event entirely for the
// full duration of a generous polling timeout, seemingly due to
// interaction with xUnit's own async-test synchronization machinery
// rather than anything about VaultWatcherService's own logic (in
// isolation, or serialized with other collections, it is reliable).
// Disabling test-collection parallelization for this assembly avoids
// that interaction - the same workaround this repo's DotNotes.Api.Tests
// project already applies (see its own AssemblyInfo.cs) for a different
// (environment-variable-sharing) reason.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
