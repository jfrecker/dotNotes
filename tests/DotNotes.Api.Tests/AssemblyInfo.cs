using Xunit;

// NotesApiFactory overrides the vault root via the process-wide
// Vault__RootPath environment variable (see its remarks). That's only
// safe if test classes in this assembly never run concurrently with each
// other, so test parallelization is disabled here.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
