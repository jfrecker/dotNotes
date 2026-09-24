namespace DotNotes.Api.Mcp;

/// <summary>
/// Single source of the task workflow guide markdown, per
/// docs/features/tasks-kanban/PLAN.md §6. Shared verbatim by the
/// <c>get_task_workflow</c> MCP tool (<see cref="DotNotesTaskMcpTools"/>)
/// and the <c>dotnotes://workflow/tasks</c> MCP resource
/// (<see cref="DotNotesTaskWorkflowResource"/>) so the two can never drift
/// apart - exactly the same pattern <c>AppInfo</c> uses to keep
/// <c>GET /api/config</c> and the MCP <c>get_config</c> tool in sync.
/// Ideas (not text) adapted from Backlog.md's MIT-licensed workflow guides
/// (<c>src/guidelines/mcp/*</c>); see NOTICE.
/// </summary>
public static class TaskWorkflowGuide
{
    public const string Markdown = """
        # dotNotes task workflow

        This guide is for AI assistants using dotNotes' task tools
        (`list_tasks`, `get_task`, `create_task`, `update_task`, `move_task`,
        `complete_task`, `get_board`, `search_tasks`). It also doubles as the
        `dotnotes://workflow/tasks` resource - the content is identical.
        (`archive_task` still exists as a deprecated alias of `complete_task`
        for older clients - use `complete_task` instead.)

        ## What a task is

        A task is an ordinary dotNotes note that happens to carry task
        frontmatter (`id`, `status`, `priority`, `assignee`, `labels`,
        `milestone`, `dependencies`, `ordinal`, ...) plus a few marked body
        sections: Description, Acceptance Criteria, Implementation Plan,
        Implementation Notes, Final Summary. It is fully editable in the app
        (kanban board or the "All Tasks" list) like any other note. A task can
        live in any folder - `create_task`'s optional `folder` defaults to
        the configured tasks folder (`Task` by default) but can be any
        vault-relative folder, e.g. `Task/ProjectX`, to keep a project's tasks
        next to its notes.

        **Never hand-edit a task's frontmatter or its marked sections via
        `update_note` / `create_note`.** Those tools treat the file as plain
        text and know nothing about ids, statuses, ordinals or the section
        markers - a manual edit can corrupt the acceptance-criteria checklist
        numbering or silently desync the file from the task index. Always go
        through the task tools listed above instead.

        Valid status and priority values are **not fixed** - they come from
        this dotNotes instance's own configuration. Call `get_board` (or
        `list_tasks`) first if you're unsure what statuses currently exist;
        it returns one column per configured status, **Backlog first**. A
        task whose status is empty, unrecognised, or the Backlog status
        itself always shows in the Backlog column (`isBacklog: true`) rather
        than in a trailing column of its own - there is no such thing as an
        "unlisted" status column any more. Task ids look like `TASK-12` (an
        id prefix plus a number) and are case-insensitive.

        ## When to create a task

        Create a task for any unit of work worth tracking to completion:
        a bug fix, a feature slice, a chore. Don't create a task for a
        single trivial edit you're about to make immediately - just make it
        (or ask the user first if unsure).

        **Always search before creating.** Call `search_tasks` (or
        `list_tasks` filtered by label/assignee/milestone) with a few
        keywords from the work you're about to describe. If an open task
        already covers it, update that one instead of creating a duplicate.

        ## Writing a task

        Write a task as a **self-contained work order** - someone (or some
        future agent) with no other context should be able to pick it up
        from the task alone:

        - **Description** = *why* this task exists and what outcome is
          wanted. Avoid prescribing *how* to implement it here - that
          belongs in the implementation plan, written later, closer to when
          the work actually starts (requirements can be stale by then).
        - **Acceptance criteria** = a short list of testable outcomes (what
          must be true when the task is done), not implementation steps.
          Each one should be checkable by an observer without reading the
          code, e.g. "Login redirects preserve the original path" rather
          than "Add a redirect parameter to LoginController".
        - Set `labels`, `priority`, `milestone`, `assignee` and
          `dependencies` when you know them; leave them unset otherwise.

        ## Execution flow

        1. `get_task` the task you're about to work on to see its current
           state - another agent or the user may have edited it since you
           last looked.
        2. `move_task` it to the in-progress-style status and `update_task`
           to set yourself as `assignee`, so it's clear the task is being
           worked and by whom.
        3. Before writing code, `update_task` with `planSet` (or
           `planAppend`) describing the approach you intend to take. Writing
           the plan down first, and only then implementing it, catches
           misunderstandings early and gives the user a chance to redirect.
        4. As you make progress, `update_task` with `notesAppend` - short,
           dated-in-spirit entries about what you did and why, especially
           anything that deviated from the plan.
        5. If you discover the task needs to grow beyond its original
           acceptance criteria, **don't silently expand scope**. Either ask
           the user, or add the new work as its own task (searching first,
           per above) and link it via `dependencies`.

        ## Finalizing a task

        - Check an acceptance criterion (`acceptanceCriteriaCheck`) **only**
          once you have real evidence it's true (you ran the test, read the
          output, or otherwise verified it) - never check it just because
          the plan said you would.
        - Once every criterion that should be met is checked, write a
          `finalSummary`: a short, PR-description-style summary of what
          changed and why, written for someone reviewing the work rather
          than someone about to redo it.
        - Move the task to its Done-like status with `move_task` if you still
          want it visible on the board for review, or call `complete_task`
          once it's truly finished: this sets its status to the configured
          completed status and moves its note into a `Completed` subfolder
          next to its current location (e.g. `Task/ProjectX/TASK-3 - X.md`
          -> `Task/ProjectX/Completed/TASK-3 - X.md`), hiding it from the
          board and `list_tasks`/`search_tasks` unless `includeCompleted` is
          set. `move_task` to a Done-like status only changes the status -
          it never moves the file - so `complete_task` is the one that
          actually archives the note.
        - `complete_task` is also fine for tasks that turned out to be
          duplicates or were cancelled outright, not just finished work.

        ## Ordering

        Board column order (top to bottom) is controlled by each task's
        `ordinal`. `move_task`'s optional `index` is a 0-based position
        within the destination column - pass it when you want a task placed
        above or below specific others; omit it to drop the task at the
        bottom of the column.
        """;
}
