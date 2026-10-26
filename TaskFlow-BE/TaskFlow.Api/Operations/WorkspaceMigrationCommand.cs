using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Infrastructure.Persistence;
using TaskFlow.Infrastructure.Workspaces;

namespace TaskFlow.Api.Operations;

public static class WorkspaceMigrationCommand
{
    public static bool IsRequested(string[] args) => args.Any(a => a.StartsWith("--workspace-backfill", StringComparison.Ordinal));

    public static async Task<int> ExecuteAsync(string[] args, TaskFlowDbContext database)
    {
        try
        {
            var options = args.ToDictionary(a => a.Split('=', 2)[0], a => a.Split('=', 2).ElementAtOrDefault(1) ?? "");
            if (options.Keys.Any(k => k is not ("--workspace-backfill" or "--manifest" or "--snapshot-id" or "--maintenance-confirmed")))
                throw new InvalidOperationException();
            var mode = options["--workspace-backfill"];
            var allowed = mode switch
            {
                "plan" => new[] { "--workspace-backfill", "--manifest", "--snapshot-id" },
                "apply" => new[] { "--workspace-backfill", "--manifest", "--maintenance-confirmed" },
                "verify" => new[] { "--workspace-backfill", "--manifest" },
                _ => throw new InvalidOperationException()
            };
            if (options.Keys.Any(k => !allowed.Contains(k))) throw new InvalidOperationException();
            if (!database.Database.GetMigrations().ToHashSet(StringComparer.Ordinal)
                .SetEquals(await database.Database.GetAppliedMigrationsAsync())) throw new InvalidOperationException();
            var path = Path.GetFullPath(options["--manifest"]);
            // The caller supplies an existing private directory. Never follow linked files/directories.
            for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory is not null; directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException();
            var backfill = new WorkspaceBackfill(database);
            if (mode == "plan")
            {
                var plan = await backfill.PlanAsync(options["--snapshot-id"]);
                var settings = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) settings.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using var output = new FileStream(path, settings);
                await JsonSerializer.SerializeAsync(output, plan, new JsonSerializerOptions { WriteIndented = true });
                Console.WriteLine($"Read-only workspace plan created: {plan.Owners.Length} owner mappings; source and mapping checksums recorded. Restrict manifest ACLs before review/apply.");
            }
            else if (mode is "apply" or "verify")
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException();
                await using var input = File.OpenRead(path);
                if (input.Length > 20 * 1024 * 1024) throw new InvalidOperationException();
                var plan = await JsonSerializer.DeserializeAsync<WorkspaceBackfillPlan>(input) ?? throw new InvalidOperationException();
                var result = mode == "apply"
                    ? await backfill.ApplyAsync(plan, options.GetValueOrDefault("--maintenance-confirmed") == "true")
                    : await backfill.VerifyAsync(plan);
                Console.WriteLine(JsonSerializer.Serialize(result));
            }
            else throw new InvalidOperationException();
            return 0;
        }
        catch
        {
            Console.Error.WriteLine("Workspace backfill refused or failed. Check exact command options, private manifest, snapshot/source consistency, maintenance state and database grants. No private diagnostics were printed; keep maintenance active and reconcile before retry.");
            return 1;
        }
    }
}
