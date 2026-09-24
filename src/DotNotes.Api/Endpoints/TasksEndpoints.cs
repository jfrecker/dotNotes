using System.Text.Json;
using DotNotes.Core.Config;
using DotNotes.Core.Notes;
using DotNotes.Core.Tasks;
using Microsoft.Extensions.Options;

namespace DotNotes.Api.Endpoints;

/// <summary>
/// Wires up the Tasks section of docs/04-API-SPEC.md (docs/features/tasks-kanban/PLAN.md
/// §4): <c>/api/tasks/*</c>. Endpoints here are intentionally thin - all
/// task parsing/validation/ordering lives in <see cref="ITaskService"/>/
/// <see cref="ITaskIndex"/> (DotNotes.Core); this file only translates
/// HTTP in/out to/from that contract, following the exact conventions of
/// <see cref="NotesEndpoints"/> (manual body parsing, the shared
/// <c>{ error, detail }</c> shape, per-file private DTO records).
/// </summary>
public static class TasksEndpoints
{
    public static WebApplication MapTasksEndpoints(this WebApplication app)
    {
        // Literal routes are registered before the "/{id}" route so they
        // are never shadowed by it - ASP.NET Core's minimal-API router
        // already prefers the more specific literal match regardless of
        // registration order, but registering them first keeps this file
        // readable/obviously-correct without relying on that.
        app.MapGet("/api/tasks/config", GetTasksConfig);
        app.MapGet("/api/tasks/revision", GetRevision);
        app.MapGet("/api/tasks/board", GetBoard);
        app.MapPost("/api/tasks/convert", PostConvertAsync);

        app.MapGet("/api/tasks", GetTasks);
        app.MapPost("/api/tasks", PostTaskAsync);
        app.MapGet("/api/tasks/{id}", GetTaskAsync);
        app.MapPatch("/api/tasks/{id}", PatchTaskAsync);
        app.MapPost("/api/tasks/{id}/move", PostMoveAsync);
        app.MapPost("/api/tasks/{id}/archive", PostArchiveAsync);

        return app;
    }

    private static IResult GetTasksConfig(IOptions<TasksOptions> options)
    {
        var value = options.Value;
        var defaultStatus = string.IsNullOrWhiteSpace(value.DefaultStatus)
            ? value.Statuses.Count > 0 ? value.Statuses[0] : null
            : value.DefaultStatus;

        return Results.Ok(new TasksConfigResponse(value.Folder, value.IdPrefix, value.Statuses, defaultStatus, value.Priorities));
    }

    private static IResult GetRevision(ITaskIndex taskIndex) =>
        Results.Ok(new RevisionResponse(taskIndex.Revision));

    private static IResult GetTasks(
        string? status, string? label, string? assignee, string? priority, string? milestone, string? q, bool? includeArchived,
        ITaskService taskService, ITaskIndex taskIndex)
    {
        var filter = BuildFilter(status, label, assignee, priority, milestone, q, includeArchived);
        var tasks = taskService.List(filter).Select(MapSummary).ToArray();
        return Results.Ok(new TaskListResponse(taskIndex.Revision, tasks));
    }

    private static IResult GetBoard(
        string? status, string? label, string? assignee, string? priority, string? milestone, string? q, bool? includeArchived,
        ITaskService taskService)
    {
        var filter = BuildFilter(status, label, assignee, priority, milestone, q, includeArchived);
        var board = taskService.GetBoard(filter);
        var columns = board.Columns.Select(c => new TaskColumnResponse(c.Status, c.Tasks.Select(MapSummary).ToArray())).ToArray();
        return Results.Ok(new TaskBoardResponse(board.Revision, columns));
    }

    private static IResult GetTaskAsync(string id, ITaskService taskService)
    {
        var task = taskService.GetById(id);
        return task is null ? NotFound(id) : Results.Ok(MapTask(task));
    }

    private static async Task<IResult> PostTaskAsync(HttpRequest request, ITaskService taskService, CancellationToken cancellationToken)
    {
        TaskCreateBody? body;
        try
        {
            body = await request.ReadFromJsonAsync<TaskCreateBody>(cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return BadRequest("invalid_body", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("invalid_body", ex.Message);
        }

        if (string.IsNullOrWhiteSpace(body?.Title))
        {
            return BadRequest("invalid_request", "Request body must include a non-empty 'title' string.");
        }

        var createRequest = new TaskCreateRequest
        {
            Title = body.Title,
            Status = body.Status,
            Description = body.Description,
            Assignee = body.Assignee,
            Labels = body.Labels,
            Priority = body.Priority,
            Milestone = body.Milestone,
            Dependencies = body.Dependencies,
            AcceptanceCriteria = body.AcceptanceCriteria,
            Folder = body.Folder,
        };

        try
        {
            var created = await taskService.CreateAsync(createRequest, cancellationToken).ConfigureAwait(false);
            return Results.Created($"/api/tasks/{created.Id}", MapTask(created));
        }
        catch (TaskValidationException ex)
        {
            return BadRequest("invalid_request", ex.Message);
        }
        catch (InvalidNotePathException ex)
        {
            return InvalidPath(ex);
        }
        catch (VaultUnavailableException ex)
        {
            return VaultUnavailable(ex);
        }
    }

    private static async Task<IResult> PatchTaskAsync(string id, HttpRequest request, ITaskService taskService, CancellationToken cancellationToken)
    {
        TaskPatchBody? body;
        try
        {
            body = await request.ReadFromJsonAsync<TaskPatchBody>(cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return BadRequest("invalid_body", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("invalid_body", ex.Message);
        }

        if (body is null)
        {
            return BadRequest("invalid_body", "Request body must be a JSON object.");
        }

        var update = new TaskUpdate
        {
            Title = body.Title,
            Status = body.Status,
            Assignee = body.Assignee,
            Labels = body.Labels,
            Priority = body.Priority,
            Milestone = body.Milestone,
            Dependencies = body.Dependencies,
            Description = body.Description,
            AcceptanceCriteria = body.AcceptanceCriteria?
                .Select(c => (c.Text, c.Checked))
                .ToArray(),
            ImplementationPlan = body.ImplementationPlan,
            ImplementationNotes = body.ImplementationNotes,
            FinalSummary = body.FinalSummary,
            Ordinal = body.Ordinal,
        };

        try
        {
            var updated = await taskService.UpdateAsync(id, update, cancellationToken).ConfigureAwait(false);
            return Results.Ok(MapTask(updated));
        }
        catch (TaskNotFoundException ex)
        {
            return NotFound(ex.TaskId);
        }
        catch (TaskValidationException ex)
        {
            return BadRequest("invalid_request", ex.Message);
        }
        catch (InvalidNotePathException ex)
        {
            return InvalidPath(ex);
        }
        catch (DestinationAlreadyExistsException ex)
        {
            return AlreadyExists(ex);
        }
        catch (VaultUnavailableException ex)
        {
            return VaultUnavailable(ex);
        }
    }

    private static async Task<IResult> PostMoveAsync(string id, HttpRequest request, ITaskService taskService, CancellationToken cancellationToken)
    {
        TaskMoveBody? body;
        try
        {
            body = await request.ReadFromJsonAsync<TaskMoveBody>(cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return BadRequest("invalid_body", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("invalid_body", ex.Message);
        }

        if (string.IsNullOrWhiteSpace(body?.Status))
        {
            return BadRequest("invalid_request", "Request body must include a non-empty 'status' string.");
        }

        if (body.Index is < 0)
        {
            return BadRequest("invalid_request", "'index' must not be negative.");
        }

        try
        {
            var moved = await taskService.MoveAsync(id, body.Status, body.Index, body.BeforeId, cancellationToken).ConfigureAwait(false);
            return Results.Ok(MapTask(moved));
        }
        catch (TaskNotFoundException ex)
        {
            return NotFound(ex.TaskId);
        }
        catch (TaskValidationException ex)
        {
            return BadRequest("invalid_request", ex.Message);
        }
        catch (VaultUnavailableException ex)
        {
            return VaultUnavailable(ex);
        }
    }

    private static async Task<IResult> PostArchiveAsync(string id, ITaskService taskService, CancellationToken cancellationToken)
    {
        try
        {
            var archived = await taskService.ArchiveAsync(id, cancellationToken).ConfigureAwait(false);
            return Results.Ok(MapTask(archived));
        }
        catch (TaskNotFoundException ex)
        {
            return NotFound(ex.TaskId);
        }
        catch (DestinationAlreadyExistsException ex)
        {
            return AlreadyExists(ex);
        }
        catch (VaultUnavailableException ex)
        {
            return VaultUnavailable(ex);
        }
    }

    private static async Task<IResult> PostConvertAsync(HttpRequest request, ITaskService taskService, CancellationToken cancellationToken)
    {
        TaskConvertBody? body;
        try
        {
            body = await request.ReadFromJsonAsync<TaskConvertBody>(cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return BadRequest("invalid_body", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest("invalid_body", ex.Message);
        }

        if (string.IsNullOrWhiteSpace(body?.Path))
        {
            return BadRequest("invalid_request", "Request body must include a non-empty 'path' string.");
        }

        try
        {
            var converted = await taskService.ConvertNoteAsync(body.Path, body.Status, cancellationToken).ConfigureAwait(false);
            return Results.Ok(MapTask(converted));
        }
        catch (TaskNotFoundException ex)
        {
            return NotFound(ex.TaskId);
        }
        catch (TaskValidationException ex)
        {
            return BadRequest("invalid_request", ex.Message);
        }
        catch (InvalidNotePathException ex)
        {
            return InvalidPath(ex);
        }
        catch (DestinationAlreadyExistsException ex)
        {
            return AlreadyExists(ex);
        }
        catch (VaultUnavailableException ex)
        {
            return VaultUnavailable(ex);
        }
    }

    private static TaskFilter BuildFilter(
        string? status, string? label, string? assignee, string? priority, string? milestone, string? q, bool? includeArchived) =>
        new()
        {
            Status = status,
            Label = label,
            Assignee = assignee,
            Priority = priority,
            Milestone = milestone,
            Query = q,
            IncludeArchived = includeArchived == true,
        };

    private static TaskSummaryResponse MapSummary(TaskItem task) => new(
        task.Id,
        task.Title,
        task.Status,
        task.Assignee,
        task.Labels,
        task.Priority,
        task.Milestone,
        task.Dependencies,
        task.CreatedDate,
        task.UpdatedDate,
        task.Ordinal,
        task.Path,
        task.Archived,
        task.Excerpt,
        task.AcceptanceCriteria.Count,
        task.AcceptanceCriteria.Count(c => c.Checked));

    private static TaskResponse MapTask(TaskItem task) => new(
        task.Id,
        task.Title,
        task.Status,
        task.Assignee,
        task.Labels,
        task.Priority,
        task.Milestone,
        task.Dependencies,
        task.CreatedDate,
        task.UpdatedDate,
        task.Ordinal,
        task.Path,
        task.Archived,
        task.Excerpt,
        task.AcceptanceCriteria.Count,
        task.AcceptanceCriteria.Count(c => c.Checked),
        task.Description,
        task.AcceptanceCriteria.Select(c => new AcceptanceCriterionResponse(c.Index, c.Text, c.Checked)).ToArray(),
        task.ImplementationPlan,
        task.ImplementationNotes,
        task.FinalSummary,
        task.UpdatedAt);

    private static IResult NotFound(string id) =>
        Results.Json(
            new ErrorResponse("not_found", $"No task exists with id '{id}'."),
            statusCode: StatusCodes.Status404NotFound);

    private static IResult InvalidPath(InvalidNotePathException ex) =>
        Results.Json(
            new ErrorResponse("invalid_path", ex.Message),
            statusCode: StatusCodes.Status400BadRequest);

    private static IResult AlreadyExists(DestinationAlreadyExistsException ex) =>
        Results.Json(
            new ErrorResponse("already_exists", ex.Message),
            statusCode: StatusCodes.Status409Conflict);

    private static IResult VaultUnavailable(VaultUnavailableException ex) =>
        Results.Json(
            new ErrorResponse("vault_unavailable", ex.Message),
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult BadRequest(string error, string? detail) =>
        Results.Json(
            new ErrorResponse(error, detail),
            statusCode: StatusCodes.Status400BadRequest);

    /// <summary>Standard error shape per docs/04-API-SPEC.md's Conventions section.</summary>
    private sealed record ErrorResponse(string Error, string? Detail = null);

    /// <summary>Backs <c>GET /api/tasks/config</c>.</summary>
    private sealed record TasksConfigResponse(string Folder, string IdPrefix, IReadOnlyList<string> Statuses, string? DefaultStatus, IReadOnlyList<string> Priorities);

    /// <summary>Backs <c>GET /api/tasks/revision</c>.</summary>
    private sealed record RevisionResponse(long Revision);

    /// <summary>Backs <c>GET /api/tasks</c>'s <c>{ revision, tasks }</c> response.</summary>
    private sealed record TaskListResponse(long Revision, IReadOnlyList<TaskSummaryResponse> Tasks);

    /// <summary>Backs <c>GET /api/tasks/board</c>'s <c>{ revision, columns }</c> response.</summary>
    private sealed record TaskBoardResponse(long Revision, IReadOnlyList<TaskColumnResponse> Columns);

    /// <summary>One entry of <see cref="TaskBoardResponse"/>'s <c>columns</c> array.</summary>
    private sealed record TaskColumnResponse(string Status, IReadOnlyList<TaskSummaryResponse> Tasks);

    /// <summary>
    /// The <c>TaskSummary</c> shape from docs/04-API-SPEC.md's Tasks
    /// section: everything about a task except its body text.
    /// </summary>
    private record TaskSummaryResponse(
        string Id,
        string Title,
        string Status,
        IReadOnlyList<string> Assignee,
        IReadOnlyList<string> Labels,
        string? Priority,
        string? Milestone,
        IReadOnlyList<string> Dependencies,
        DateTimeOffset? CreatedDate,
        DateTimeOffset? UpdatedDate,
        double? Ordinal,
        string Path,
        bool Archived,
        string Excerpt,
        int AcTotal,
        int AcChecked);

    /// <summary>
    /// The <c>Task</c> shape from docs/04-API-SPEC.md's Tasks section:
    /// <c>TaskSummary</c> plus body-text fields, returned by every
    /// endpoint that returns a single task.
    /// </summary>
    private sealed record TaskResponse(
        string Id,
        string Title,
        string Status,
        IReadOnlyList<string> Assignee,
        IReadOnlyList<string> Labels,
        string? Priority,
        string? Milestone,
        IReadOnlyList<string> Dependencies,
        DateTimeOffset? CreatedDate,
        DateTimeOffset? UpdatedDate,
        double? Ordinal,
        string Path,
        bool Archived,
        string Excerpt,
        int AcTotal,
        int AcChecked,
        string Description,
        IReadOnlyList<AcceptanceCriterionResponse> AcceptanceCriteria,
        string? ImplementationPlan,
        string? ImplementationNotes,
        string? FinalSummary,
        DateTimeOffset UpdatedAt)
        : TaskSummaryResponse(Id, Title, Status, Assignee, Labels, Priority, Milestone, Dependencies, CreatedDate, UpdatedDate, Ordinal, Path, Archived, Excerpt, AcTotal, AcChecked);

    /// <summary>One entry of <see cref="TaskResponse"/>'s <c>acceptanceCriteria</c> array.</summary>
    private sealed record AcceptanceCriterionResponse(int Index, string Text, bool Checked);

    /// <summary>Request body shape for <c>POST /api/tasks</c> (<c>TaskCreate</c>).</summary>
    private sealed record TaskCreateBody(
        string? Title,
        string? Status,
        string? Description,
        IReadOnlyList<string>? Assignee,
        IReadOnlyList<string>? Labels,
        string? Priority,
        string? Milestone,
        IReadOnlyList<string>? Dependencies,
        IReadOnlyList<string>? AcceptanceCriteria,
        string? Folder);

    /// <summary>
    /// Request body shape for <c>PATCH /api/tasks/{id}</c> (<c>TaskPatch</c>).
    /// Every field is optional; a nullable-string field left absent in the
    /// JSON deserializes to <see langword="null"/> ("unchanged" per
    /// <see cref="TaskUpdate"/>'s patch semantics), while an explicit
    /// <c>""</c> deserializes to an empty string ("clear this field") -
    /// the two are distinguishable because these are reference-typed
    /// properties, not passed through any additional default-value logic.
    /// </summary>
    private sealed record TaskPatchBody(
        string? Title,
        string? Status,
        IReadOnlyList<string>? Assignee,
        IReadOnlyList<string>? Labels,
        string? Priority,
        string? Milestone,
        IReadOnlyList<string>? Dependencies,
        string? Description,
        IReadOnlyList<AcceptanceCriterionPatchBody>? AcceptanceCriteria,
        string? ImplementationPlan,
        string? ImplementationNotes,
        string? FinalSummary,
        double? Ordinal);

    /// <summary>One entry of <see cref="TaskPatchBody"/>'s <c>acceptanceCriteria</c> array: <c>{ text, checked }</c>.</summary>
    private sealed record AcceptanceCriterionPatchBody(string Text, bool Checked);

    /// <summary>Request body shape for <c>POST /api/tasks/{id}/move</c>: <c>{ status, index?, beforeId? }</c>.</summary>
    private sealed record TaskMoveBody(string? Status, int? Index, string? BeforeId);

    /// <summary>Request body shape for <c>POST /api/tasks/convert</c>: <c>{ path, status? }</c>.</summary>
    private sealed record TaskConvertBody(string? Path, string? Status);
}
