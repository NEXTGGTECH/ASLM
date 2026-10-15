// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Models;

/// <summary>
/// One permitted documentation file in the root of a module's GitHub repository.
/// </summary>
internal sealed record ModuleRepositoryDocument(string Name, string Sha);
