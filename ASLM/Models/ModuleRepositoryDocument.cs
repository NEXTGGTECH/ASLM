// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Models
{
    /// <summary>
    /// One permitted documentation file in the root of a module's GitHub repository.
    /// </summary>
    /// <param name="Name">File name of the document in the repository root.</param>
    /// <param name="Sha">Git blob SHA of the file; part of the content cache key, so a changed file is fetched again.</param>
    internal sealed record ModuleRepositoryDocument(string Name, string Sha);
}
