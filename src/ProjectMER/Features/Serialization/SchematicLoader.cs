using System.Diagnostics;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ProjectMER.Features.Mobile;
using ProjectMER.Features.Serializable.Schematics;
using FileStamp = ProjectMER.Features.Serialization.SchematicJson.FileStamp;

namespace ProjectMER.Features.Serialization;

/// <summary>
/// Reads schematic files and plans them on worker threads, so loading a schematic does not stall the server frame
/// (docs/projectmer-port-plan.md §3.5).
/// </summary>
/// <remarks>
/// <para>
/// A worker reads the JSON file (and <c>&lt;name&gt;-Rigidbodies.json</c>), parses it into
/// <see cref="SchematicObjectDataList"/> and runs <see cref="SchematicOptimizer.Build"/>: pure data, no Unity or plugin
/// state. The main thread polls <see cref="SchematicLoadJob.IsCompleted"/> and calls <see cref="SchematicLoadJob.Publish"/>,
/// which puts the results into the <see cref="SchematicJson"/> cache. Cached files and plans complete immediately.
/// </para>
/// <para>
/// Concurrent loads of the same file share one job. All members are main-thread only.
/// </para>
/// </remarks>
public static class SchematicLoader
{
	private static readonly List<SchematicLoadJob> InFlight = [];

	/// <summary>
	/// Gets the number of jobs still running or waiting to be published.
	/// </summary>
	public static int PendingJobs => InFlight.Count;

	/// <summary>
	/// Finds a schematic's files. Like <c>MapUtils.GetSchematicDataByName</c>, a <c>&lt;name&gt;.json</c> placed directly
	/// in the schematics folder is moved into its own directory.
	/// </summary>
	/// <param name="schematicName">The schematic name.</param>
	/// <param name="directory">The schematic directory.</param>
	/// <param name="jsonPath">The JSON file.</param>
	/// <param name="error">Why the schematic cannot be loaded.</param>
	/// <returns>Whether the JSON file exists.</returns>
	public static bool TryResolve(string schematicName, out string directory, out string jsonPath, out string error)
	{
		// A name with folders could reach (and move) any JSON file on the server.
		if (!MapUtils.IsValidName(schematicName, out error))
		{
			directory = jsonPath = string.Empty;
			return false;
		}

		directory = Path.Combine(ProjectMER.SchematicsDir, schematicName);
		jsonPath = Path.Combine(directory, $"{schematicName}.json");
		string misplaced = directory + ".json";
		error = string.Empty;

		try
		{
			if (!Directory.Exists(directory))
			{
				if (!File.Exists(misplaced))
				{
					error = $"Failed to load schematic data: Directory {schematicName} does not exist!";
					return false;
				}

				Directory.CreateDirectory(directory);
				File.Move(misplaced, jsonPath);
				return true;
			}

			if (!File.Exists(jsonPath))
			{
				if (!File.Exists(misplaced))
				{
					error = $"Failed to load schematic data: File {schematicName}.json does not exist!";
					return false;
				}

				File.Move(misplaced, jsonPath);
			}

			return true;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			error = $"Failed to load schematic data for {schematicName}: {e.Message}";
			return false;
		}
	}

	/// <summary>
	/// Gets a schematic's parsed data if the cache holds it for the file as it is now.
	/// </summary>
	/// <param name="directory">The schematic directory.</param>
	/// <param name="jsonPath">The JSON file.</param>
	/// <param name="data">The cached data (shared; read only).</param>
	internal static bool TryGetCached(string directory, string jsonPath, out SchematicObjectDataList data)
	{
		if (!SchematicJson.TryGetCached(jsonPath, FileStamp.Of(jsonPath), out data))
			return false;

		data.Path = directory;
		return true;
	}

	/// <summary>
	/// Loads a schematic file, and plans it when <paramref name="settings"/> is given.
	/// </summary>
	/// <param name="name">The schematic name.</param>
	/// <param name="directory">The schematic directory.</param>
	/// <param name="jsonPath">The JSON file.</param>
	/// <param name="settings">The settings to plan with, or <see langword="null"/> to only parse.</param>
	/// <returns>The job; complete already when the file (and plan) are cached.</returns>
	internal static SchematicLoadJob LoadFile(string name, string directory, string jsonPath, OptimizerSettings? settings)
	{
		FileStamp stamp = FileStamp.Of(jsonPath);
		if (SchematicJson.TryGetCached(jsonPath, stamp, out SchematicObjectDataList cached))
		{
			cached.Path = directory;
			if (settings == null)
				return SchematicLoadJob.FromCache(name, cached, null);

			return LoadPlan(cached, settings.Value);
		}

		foreach (SchematicLoadJob job in InFlight)
		{
			if (job.Matches(jsonPath, stamp, settings))
				return job;
		}

		string rigidbodiesPath = GetRigidbodiesPath(directory, name);
		SchematicLoadJob created = SchematicLoadJob.StartFile(name, directory, jsonPath, stamp, settings, rigidbodiesPath, FileStamp.Of(rigidbodiesPath));
		InFlight.Add(created);
		return created;
	}

	/// <summary>
	/// Plans schematic data. Plans of cached data are cached per settings and rigidbody file.
	/// </summary>
	/// <param name="data">The data (read only while the job runs).</param>
	/// <param name="settings">The optimizer settings.</param>
	/// <returns>The job; complete already when the plan is cached.</returns>
	internal static SchematicLoadJob LoadPlan(SchematicObjectDataList data, OptimizerSettings settings)
	{
		string name = data.Path != null ? Path.GetFileName(data.Path) : "schematic";
		string? rigidbodiesPath = data.Path != null ? GetRigidbodiesPath(data.Path, name) : null;
		FileStamp rigidbodiesStamp = FileStamp.Of(rigidbodiesPath);
		PlanKey key = new(settings, rigidbodiesStamp);

		Dictionary<PlanKey, SchematicBuildPlan>? plans = SchematicJson.GetDerivedTable<PlanKey, SchematicBuildPlan>(data);
		if (plans != null && plans.TryGetValue(key, out SchematicBuildPlan plan))
			return SchematicLoadJob.FromCache(name, data, plan);

		foreach (SchematicLoadJob job in InFlight)
		{
			if (job.Matches(data, key))
				return job;
		}

		SchematicLoadJob created = SchematicLoadJob.StartPlan(name, data, settings, rigidbodiesPath, rigidbodiesStamp);
		InFlight.Add(created);
		return created;
	}

	/// <summary>
	/// Publishes a finished job's results into the cache.
	/// </summary>
	internal static void OnPublished(SchematicLoadJob job)
	{
		InFlight.Remove(job);

		SchematicObjectDataList? data = job.Data;
		if (data == null)
			return;

		if (job.JsonPath != null)
			SchematicJson.Store(job.JsonPath, job.JsonStamp, data);

		if (job.Plan != null)
		{
			Dictionary<PlanKey, SchematicBuildPlan>? plans = SchematicJson.GetDerivedTable<PlanKey, SchematicBuildPlan>(data);
			if (plans != null)
				plans[new PlanKey(job.Plan.Settings, job.RigidbodiesStamp)] = job.Plan;
		}
	}

	/// <summary>
	/// Drops pending jobs (round restart). Running workers finish on their own; nothing publishes them.
	/// </summary>
	internal static void Clear() => InFlight.Clear();

	private static string GetRigidbodiesPath(string directory, string name) => Path.Combine(directory, $"{name}-Rigidbodies.json");

	/// <summary>
	/// Identifies a cached plan.
	/// </summary>
	internal readonly record struct PlanKey(OptimizerSettings Settings, FileStamp Rigidbodies);
}

/// <summary>
/// A schematic parse and/or plan running on a worker thread (see <see cref="SchematicLoader"/>).
/// </summary>
public sealed class SchematicLoadJob
{
	private Task? _task;

	private bool _published;

	private SchematicLoadJob(string name) => Name = name;

	/// <summary>
	/// Gets the schematic name.
	/// </summary>
	public string Name { get; }

	/// <summary>
	/// Gets the parsed data, once complete (or the input data of a plan job).
	/// </summary>
	public SchematicObjectDataList? Data { get; private set; }

	/// <summary>
	/// Gets the plan, once complete, if the job plans.
	/// </summary>
	public SchematicBuildPlan? Plan { get; private set; }

	/// <summary>
	/// Gets the error message, if the job failed.
	/// </summary>
	public string? Error { get; private set; }

	/// <summary>
	/// Gets whether the results came from the cache without a worker.
	/// </summary>
	public bool WasCached { get; private set; }

	/// <summary>
	/// Gets the time the worker spent parsing JSON.
	/// </summary>
	public double ParseMilliseconds { get; private set; }

	/// <summary>
	/// Gets whether the worker finished.
	/// </summary>
	public bool IsCompleted => _task == null || _task.IsCompleted;

	internal string? JsonPath { get; private set; }

	internal SchematicJson.FileStamp JsonStamp { get; private set; }

	internal SchematicJson.FileStamp RigidbodiesStamp { get; private set; }

	private OptimizerSettings? Settings { get; set; }

	/// <summary>
	/// Blocks until the worker finished (used when a plugin needs a schematic synchronously).
	/// </summary>
	public void Wait()
	{
		try
		{
			_task?.Wait();
		}
		catch (AggregateException)
		{
			// The worker records its own errors.
		}
	}

	/// <summary>
	/// Puts the results into the cache. Main thread; call once <see cref="IsCompleted"/>; repeated calls do nothing.
	/// </summary>
	public void Publish()
	{
		if (_published || !IsCompleted)
			return;

		_published = true;
		SchematicLoader.OnPublished(this);
	}

	internal static SchematicLoadJob FromCache(string name, SchematicObjectDataList data, SchematicBuildPlan? plan) =>
		new(name) { Data = data, Plan = plan, WasCached = true, _published = true };

	internal static SchematicLoadJob StartFile(string name, string directory, string jsonPath, SchematicJson.FileStamp stamp, OptimizerSettings? settings, string rigidbodiesPath, SchematicJson.FileStamp rigidbodiesStamp)
	{
		SchematicLoadJob job = new(name)
		{
			JsonPath = jsonPath,
			JsonStamp = stamp,
			Settings = settings,
			RigidbodiesStamp = rigidbodiesStamp,
		};

		job._task = Task.Run(() => job.RunFile(directory, jsonPath, rigidbodiesPath));
		return job;
	}

	internal static SchematicLoadJob StartPlan(string name, SchematicObjectDataList data, OptimizerSettings settings, string? rigidbodiesPath, SchematicJson.FileStamp rigidbodiesStamp)
	{
		SchematicLoadJob job = new(name)
		{
			Data = data,
			Settings = settings,
			RigidbodiesStamp = rigidbodiesStamp,
		};

		job._task = Task.Run(() => job.RunPlan(rigidbodiesPath));
		return job;
	}

	internal bool Matches(string jsonPath, SchematicJson.FileStamp stamp, OptimizerSettings? settings) =>
		!_published && JsonPath != null && string.Equals(JsonPath, jsonPath, StringComparison.OrdinalIgnoreCase) && JsonStamp == stamp && Settings == settings;

	internal bool Matches(SchematicObjectDataList data, SchematicLoader.PlanKey key) =>
		!_published && JsonPath == null && ReferenceEquals(Data, data) && Settings == key.Settings && RigidbodiesStamp == key.Rigidbodies;

	private void RunFile(string directory, string jsonPath, string rigidbodiesPath)
	{
		try
		{
			long start = Stopwatch.GetTimestamp();
			JsonSerializer serializer = SchematicJson.CreateSerializer();
			SchematicObjectDataList data = SchematicJson.DeserializeFile<SchematicObjectDataList>(jsonPath, serializer);
			if (data == null)
			{
				Error = $"Failed to load schematic data: File {Name}.json is empty!";
				return;
			}

			data.Path = directory;
			data.Blocks ??= [];
			ParseMilliseconds = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
			Data = data;

			if (Settings != null)
				Plan = BuildPlan(data, Settings.Value, rigidbodiesPath, serializer);

			if (Plan != null)
				Plan.ParseMilliseconds = ParseMilliseconds;
		}
		catch (JsonException e)
		{
			Error = $"Failed to load schematic data: File {Name}.json has JSON errors!\n{e.ToString().Split('\n')[0]}";
		}
		catch (Exception e)
		{
			Error = $"Failed to load schematic {Name}: {e}";
		}
	}

	private void RunPlan(string? rigidbodiesPath)
	{
		try
		{
			Plan = BuildPlan(Data!, Settings!.Value, rigidbodiesPath, null);
		}
		catch (Exception e)
		{
			Error = $"Failed to plan schematic {Name}: {e}";
		}
	}

	private SchematicBuildPlan BuildPlan(SchematicObjectDataList data, OptimizerSettings settings, string? rigidbodiesPath, JsonSerializer? serializer)
	{
		Dictionary<int, SerializableRigidbody>? rigidbodies = null;
		if (rigidbodiesPath != null && File.Exists(rigidbodiesPath))
		{
			try
			{
				rigidbodies = SchematicJson.DeserializeFile<Dictionary<int, SerializableRigidbody>>(rigidbodiesPath, serializer ?? SchematicJson.CreateSerializer());
			}
			catch (Exception e)
			{
				// As before: a broken rigidbody file leaves the schematic static.
				RigidbodiesError = $"failed to read {Path.GetFileName(rigidbodiesPath)}: {e.Message}";
			}
		}

		return SchematicOptimizer.Build(data, settings, rigidbodies);
	}

	/// <summary>
	/// Gets the problem with the rigidbody file, if it could not be read (the schematic then stays static).
	/// </summary>
	public string? RigidbodiesError { get; private set; }
}
