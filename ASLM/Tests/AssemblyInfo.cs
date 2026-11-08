// Copyright NEXTGGTECH. Apache License 2.0.

using Xunit;

// Tests share process-wide state (environment variables, the ASLM root override and the shared data files),
// so xUnit must not run test classes in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
