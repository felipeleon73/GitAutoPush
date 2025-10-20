
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibGit2Sharp;

namespace GitAutoPush
{
    class Program
    {
        // Configurazione da variabili d'ambiente
        private static readonly string RepoPath =
            Environment.GetEnvironmentVariable("REPO_PATH") ?? "/repo";
        private static readonly string AuthorName =
            Environment.GetEnvironmentVariable("AUTHOR_NAME") ?? throw new Exception("Missing AUTHOR_NAME env variable");
        private static readonly string AuthorEmail =
            Environment.GetEnvironmentVariable("AUTHOR_EMAIL") ?? throw new Exception("Missing AUTHOR_EMAIL env variable");

        private static readonly int CheckIntervalMinutes =
            int.TryParse(Environment.GetEnvironmentVariable("CHECK_INTERVAL_MINUTES"), out var ci) ? ci : 5;
        private static readonly int InactivityThresholdMinutes =
            int.TryParse(Environment.GetEnvironmentVariable("INACTIVITY_THRESHOLD_MINUTES"), out var it) ? it : 60;

        static async Task Main(string[] args)
        {
            // Log su stdout (viene catturato da journald)
            Console.WriteLine("=== Git Auto Push Monitor ===");
            Console.WriteLine($"Repository: {RepoPath}");
            Console.WriteLine($"Check interval: {CheckIntervalMinutes} minutes");
            Console.WriteLine($"Push after: {InactivityThresholdMinutes} minutes of inactivity");

            if (!Directory.Exists(Path.Combine(RepoPath, ".git")))
            {
                Console.WriteLine("ERROR: Invalid Git repository path!");
                Environment.Exit(1);
            }

            var cts = new CancellationTokenSource();
            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                cts.Cancel();
                Console.WriteLine("Shutting down...");
            };

            await MonitorRepositoryAsync(cts.Token);
        }

        static async Task MonitorRepositoryAsync(CancellationToken cancellationToken)
        {
            var repoStatus = new RepoStatus(null, []);

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Checking repository...");

                    using (var repo = new Repository(RepoPath))
                    {
                        var currentRepoStatus = GetRepoStatus(repo, RepoPath);
                        if (!currentRepoStatus.HasChange)
                        {
                            Console.WriteLine("  No changes detected.");
                        }
                        else
                        {
                            var deleteChangesDate = !repoStatus.DeletedFiles.SequenceEqual(currentRepoStatus.DeletedFiles) ? DateTime.Now : DateTime.MinValue;
                            var lastFilesChangeDate = currentRepoStatus.LastChange ?? DateTime.MinValue;
                            var previousDetectedChangeDate = repoStatus.LastChange ?? DateTime.MinValue;
                            var detectedChangeDate = new[] { deleteChangesDate, lastFilesChangeDate, previousDetectedChangeDate }.Max();
                            if (detectedChangeDate > previousDetectedChangeDate)
                            {
                                Console.WriteLine($"  New changes detected at {detectedChangeDate:yyyy-MM-dd HH:mm:ss}");
                            }
                            var inactivityMinutes = (DateTime.Now - detectedChangeDate).TotalMinutes;
                            Console.WriteLine($"  Changes staged. Inactivity: {inactivityMinutes:F1} minutes");
                            if (inactivityMinutes >= InactivityThresholdMinutes)
                            {
                                Console.WriteLine($"  Inactivity threshold reached. Committing and pushing...");
                                CommitAndPush(repo);
                                Console.WriteLine("  ✓ Push completed successfully!");
                                repoStatus = new(null, []);
                            }
                            else { 
                                repoStatus = new RepoStatus(detectedChangeDate, currentRepoStatus.DeletedFiles);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ERROR: {ex.Message}");
                    Console.WriteLine($"  Stack trace: {ex.StackTrace}");
                }

                Console.WriteLine($"  Next check in {CheckIntervalMinutes} minutes...");
                await Task.Delay(TimeSpan.FromMinutes(CheckIntervalMinutes), cancellationToken); 
            }
        }
        static void CommitAndPush(Repository repo)
        {
            var message = $"push automatico {DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
            var signature = new Signature(AuthorName, AuthorEmail, DateTimeOffset.Now);
            var commit = repo.Commit(message, signature, signature);
            Console.WriteLine($"  Commit created: {commit.Sha.Substring(0, 7)}");

            repo.Network.Push(repo.Head);
        }

        static RepoStatus GetRepoStatus(Repository repo, string repoPath)
        {
            Commands.Stage(repo, "*");
            var status = repo.RetrieveStatus();
            var lastModifiedDateTime = GetLastModifiedDateTime(status, repoPath);
            var removedFiles = status.Removed.Select(x => x.FilePath).ToImmutableHashSet();
            return new RepoStatus(lastModifiedDateTime, removedFiles)   ;
        }

        static DateTime? GetLastModifiedDateTime(RepositoryStatus status, string repoPath)
        {
            var modifiedDateTime = status.Modified
                .Concat(status.Added)
                .Concat(status.Staged)
                .Concat(status.Untracked)
                .Select(item => Path.Combine(repoPath, item.FilePath))
                .Where(File.Exists)
                .Select(File.GetLastWriteTime);
            return modifiedDateTime.Any() ? modifiedDateTime.Max() : null;
        }
    }
}

public record RepoStatus (DateTime? LastChange, ImmutableHashSet<string> DeletedFiles)
{
    public bool HasChange => LastChange.HasValue || DeletedFiles.Any();
}
