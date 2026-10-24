// Copyright NEXTGGTECH. Apache License 2.0.

using Xunit;

// The suite mutates process-global state (TMP/TEMP, ASLM_INSTALLER_PAYLOAD_PATH, static
// PatcherRunner fields), so tests within this assembly must run sequentially like ASLM.Tests.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
