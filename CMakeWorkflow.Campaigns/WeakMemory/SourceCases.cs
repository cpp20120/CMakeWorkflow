namespace CMakeWorkflow.Campaigns;

internal sealed record RelacySuite(string Name, string[] Expected);

internal sealed record RelacyVariant(string Name, string Header, RelacySuite[] Suites);

internal sealed class SourceCases(string root)
{
    private readonly SourceAdapters adapters = new(root);
    private static readonly string[] Failure = ["USER ASSERT FAILED", "DATA RACE"];
    private static readonly string[] LostWake = ["USER ASSERT FAILED", "DATA RACE", "DEADLOCK"];

    private static RelacySuite[] Check(string[] names, string[] expected) =>
        names.Select(name => new RelacySuite(name, expected)).ToArray();

    private static RelacyVariant Variant(string name, string header, string[] names, string[] expected) =>
        new(name, header, Check(names, expected));

    private static string Change(string source, string signature, string before, string after)
    {
        var body = SourceAdapters.Definition(source, signature);
        return SourceAdapters.Once(source, body, SourceAdapters.Once(body, before, after));
    }

    private static string Once(string source, string before, string after) =>
        SourceAdapters.Once(source, before, after);

    internal static readonly IReadOnlyDictionary<string, string[]> Sources =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["deque"] = ["include/dagflow/detail/chase_lev_deque.hpp"],
            ["parking"] = ["include/dagflow/detail/parking_lot.hpp", "src/parking_lot.cpp"],
            ["completion"] = ["include/dagflow/handle.hpp", "src/thread_pool.cpp"],
            ["idle_accounting"] = ["include/dagflow/detail/idle_accounting.hpp", "src/idle_accounting.cpp"],
            ["shutdown"] =
            [
                "include/dagflow/detail/idle_accounting.hpp", "src/idle_accounting.cpp",
                "include/dagflow/thread_pool.hpp", "src/thread_pool.cpp"
            ]
        };

    internal IReadOnlyList<RelacyVariant> Generate(string protocol)
    {
        var baseHeader = adapters.Baseline(protocol);
        var variants = new List<RelacyVariant>();

        void Add(string name, string header, string[] names, string[] expected) =>
            variants.Add(Variant(name, header, names, expected));

        switch (protocol)
        {
            case "deque":
            {
                Add("baseline", baseHeader, ["publication", "slot-publication", "last-item", "two-items"], ["SUCCESS"]);
                var needle =
                    "DAGFLOW_FUZZ_POINT(deque_before_publish);\n    bottom_.store(b + 1, rl::memory_order_release);";
                Add("deque-relax-publication", Once(baseHeader, needle, needle.Replace("release", "relaxed")),
                    ["publication", "slot-publication"], Failure);
                break;
            }
            case "parking":
            {
                Add("baseline", baseHeader, ["announce", "sleep", "wake"], ["SUCCESS"]);
                Add("parking-drop-publisher-fence",
                    Change(baseHeader, "void ParkingLot::wake_one(",
                        "rl::atomic_thread_fence(rl::memory_order_seq_cst);",
                        "/* no publisher fence */"), ["announce"], Failure);
                var changed = baseHeader;
                foreach (var before in new[]
                         {
                             "waiter.sleeping.store(true, rl::memory_order_seq_cst)",
                             "waiter.epoch.load(rl::memory_order_seq_cst) != epoch",
                             "waiter.epoch.fetch_add(1, rl::memory_order_seq_cst)",
                             "waiter.sleeping.load(rl::memory_order_seq_cst)"
                         })
                    changed = Once(changed, before, before.Replace("seq_cst", "relaxed"));
                Add("parking-relax-sleep-handshake", changed, ["sleep"], LostWake);
                Add("parking-drop-cv-handoff", Once(baseHeader,
                    "  {\n    rl::lock_guard<rl::mutex> lock(waiter.mutex);\n  }",
                    "  /* no CV enrollment handshake */"), ["sleep"], LostWake);
                Add("parking-drop-notify", Once(baseHeader, "  waiter.cv.notify_one();",
                    "  /* missing notify */"), ["sleep"], LostWake);
                break;
            }
            case "completion":
            {
                Add("baseline", baseHeader,
                    ["aggregate", "ready", "wait", "registration", "registration-error", "cascade"], ["SUCCESS"]);
                var needle = "state->credits.fetch_sub(1, rl::memory_order_acq_rel)";
                Add("completion-retire-release-only", Once(baseHeader, needle,
                    needle.Replace("acq_rel", "release")), ["aggregate"], Failure);
                needle = "current->ready.store(true, rl::memory_order_release)";
                Add("completion-ready-relaxed", Once(baseHeader, needle,
                    needle.Replace("release", "relaxed")), ["ready"], Failure);
                needle =
                    "current->dependent_gate.exchange(Dependents::closed,\n                                         rl::memory_order_acq_rel)";
                Add("completion-gate-close-relaxed", Once(baseHeader, needle,
                    needle.Replace("acq_rel", "relaxed")), ["registration"], Failure);
                var changed = Change(baseHeader, "void CompletionState::add_dependent(",
                    "rl::memory_order_acquire);", "rl::memory_order_relaxed);");
                Add("completion-registration-failure-relaxed", changed, ["registration"], Failure);
                needle = "dependent->credits.fetch_sub(1, rl::memory_order_acq_rel)";
                changed = Change(baseHeader, "void CompletionState::model_finish_terminal(",
                    needle, needle.Replace("acq_rel", "release"));
                changed = Change(changed, "void model_deliver_edge(",
                    needle, needle.Replace("acq_rel", "release"));
                Add("completion-dependent-retire-release-only", changed, ["cascade"], Failure);
                break;
            }
            case "idle_accounting":
            {
                Add("baseline", baseHeader, [
                    "visibility", "aggregate", "handoff", "external-handoff",
                    "wait", "multiple-waiters", "repeated-waiters"
                ], ["SUCCESS"]);
                var needle = "retired.store(previous + 1, rl::memory_order_release)";
                Add("idle-retire-relaxed", Once(baseHeader, needle, needle.Replace("release", "relaxed")),
                    ["visibility", "aggregate"], Failure);
                needle = "retired_before.add(\n        workers_[worker_index].retired.load(rl::memory_order_acquire))";
                Add("idle-first-collect-relaxed", Once(baseHeader, needle, needle.Replace("acquire", "relaxed")),
                    ["handoff", "external-handoff"], Failure);
                needle = "return retired_before == retired_after && published_total == retired_after;";
                Add("idle-drop-stability-check", Once(baseHeader, needle,
                    "return published_total == retired_after;"), ["handoff", "external-handoff"], Failure);
                foreach (var (name, signature) in new[]
                         {
                             ("worker", "void IdleAccounting::worker_idle("),
                             ("waiter", "void IdleAccounting::wait(")
                         })
                    Add("idle-drop-" + name + "-fence",
                        Change(baseHeader, signature, "rl::atomic_thread_fence(rl::memory_order_seq_cst);",
                            "/* missing announcement fence */"), ["wait"], LostWake);
                Add("idle-drop-cv-handoff", Once(baseHeader,
                    "  {\n    rl::lock_guard<rl::mutex> lock(mutex_);\n  }",
                    "  /* no CV enrollment handshake */"), ["wait"], LostWake);
                Add("idle-drop-notify", Once(baseHeader, "  cv_.notify_all();",
                    "  /* missing notify */"), ["wait"], LostWake);
                Add("idle-notify-one", Once(baseHeader, "cv_.notify_all();", "cv_.notify_one();"),
                    ["multiple-waiters"], LostWake);
                needle = "waiters_.fetch_add(1, rl::memory_order_relaxed)";
                Add("idle-overwrite-waiter-count", Once(baseHeader, needle,
                    "waiters_.store(1, rl::memory_order_relaxed)"), ["multiple-waiters"], Failure);
                break;
            }
            case "shutdown":
            {
                Add("baseline", baseHeader, [
                    "admission", "close-race", "descendant-drain",
                    "publication-drain", "shutdown-with-waiter"
                ], ["SUCCESS"]);
                var body = SourceAdapters.Definition(baseHeader, "void Pool::shutdown(");
                var barrier = SourceAdapters.Definition(body, "while (state != admission_closed_bit)");
                Add("shutdown-drop-admission-drain", Once(baseHeader, body,
                        Once(body, barrier, "/* missing publisher drain */")),
                    ["admission", "publication-drain"], Failure);
                var needle = "admission_.fetch_sub(1, rl::memory_order_release)";
                Add("shutdown-relax-publisher-release", Once(baseHeader, needle,
                    needle.Replace("release", "relaxed")), ["admission"], Failure);
                if (body.Split("rl::memory_order_acquire").Length - 1 != 3)
                    throw new InvalidDataException("Expected three acquire observations in shutdown.");
                var changed = Once(baseHeader, body, body.Replace("rl::memory_order_acquire",
                    "rl::memory_order_relaxed", StringComparison.Ordinal));
                Add("shutdown-relax-admission-acquire", changed, ["admission"], ["SUCCESS"]);
                changed = Change(changed, "void IdleAccounting::wait(",
                    "rl::atomic_thread_fence(rl::memory_order_seq_cst);",
                    "/* also remove the later acquire fence */");
                Add("shutdown-relax-admission-and-wait-fence", changed, ["admission"], Failure);
                Add("shutdown-drop-physical-drain", Change(baseHeader, "void Pool::shutdown(",
                        "  accounting_->wait();", "  /* missing physical drain */"),
                    ["descendant-drain", "publication-drain"], Failure);
                needle = "  accounting_->wait();\n  stop_.store(true, rl::memory_order_release);";
                Add("shutdown-stop-before-drain", Once(baseHeader, needle,
                        "  stop_.store(true, rl::memory_order_release);\n  accounting_->wait();"),
                    ["descendant-drain", "publication-drain"], Failure);
                changed = Once(body, "  accounting_->wait();", "  /* drain moved before admission */");
                changed = Once(changed, "  close();", "  accounting_->wait();\n  close();");
                Add("shutdown-idle-before-admission", Once(baseHeader, body, changed),
                    ["publication-drain"], Failure);
                Add("shutdown-ignore-closed", Change(baseHeader, "void Pool::enter_publication(",
                    "if (state & admission_closed_bit)", "if (false)"), ["close-race"], Failure);
                Add("shutdown-notify-one", Once(baseHeader, "cv_.notify_all();",
                    "cv_.notify_one();"), ["shutdown-with-waiter"], LostWake);
                break;
            }
            default: throw new ArgumentException("Unknown protocol: " + protocol);
        }

        return variants;
    }
}