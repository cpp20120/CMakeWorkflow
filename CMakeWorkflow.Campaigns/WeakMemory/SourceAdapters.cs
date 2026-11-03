using System.Text.RegularExpressions;

namespace CMakeWorkflow.Campaigns;

// DagFlow-specific source adaptation. The orchestration core never interprets C++.
internal sealed class SourceAdapters(string root)
{
    private string Read(string path) => File.ReadAllText(Path.Combine(root, path));

    internal static string Once(string source, string before, string after)
    {
        var first = source.IndexOf(before, StringComparison.Ordinal);
        if (first < 0 || source.IndexOf(before, first + before.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidDataException("Expected unique source anchor: " + before[..Math.Min(100, before.Length)]);
        return source[..first] + after + source[(first + before.Length)..];
    }

    internal static string Definition(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0 || source.IndexOf(signature, start + signature.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidDataException("Expected unique definition: " + signature);
        var opening = source.IndexOf('{', start);
        if (opening < 0) throw new InvalidDataException("Missing definition body: " + signature);
        var tokens = Regex.Matches(source[opening..],
            @"//[^\n]*|/\*.*?\*/|""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|[{}]",
            RegexOptions.Singleline);
        var depth = 0;
        foreach (Match match in tokens)
        {
            if (match.Value == "{") depth++;
            else if (match.Value == "}" && --depth == 0)
                return source[start..(opening + match.Index + match.Length)];
        }

        throw new InvalidDataException("Unbalanced definition: " + signature);
    }

    internal static string Atomics(string source)
    {
        source = source.Replace("std::atomic<", "ModelAtomic<", StringComparison.Ordinal)
            .Replace("std::memory_order_", "rl::memory_order_", StringComparison.Ordinal)
            .Replace("std::atomic_thread_fence", "rl::atomic_thread_fence", StringComparison.Ordinal)
            .Replace("std::mutex", "rl::mutex", StringComparison.Ordinal)
            .Replace("std::lock_guard lock", "rl::lock_guard<rl::mutex> lock", StringComparison.Ordinal)
            .Replace("std::unique_lock lock", "rl::unique_lock<rl::mutex> lock", StringComparison.Ordinal);
        return Regex.Replace(source, @"\bassert\(", "RL_ASSERT(");
    }

    internal string Baseline(string protocol) => protocol switch
    {
        "deque" => Atomics(Read("include/dagflow/detail/chase_lev_deque.hpp")),
        "parking" => Parking(),
        "completion" => Completion(),
        "idle_accounting" => IdleAccounting(),
        "shutdown" => Shutdown(),
        _ => throw new ArgumentException("Unknown Relacy protocol: " + protocol)
    };

    private string Parking()
    {
        var source = Read("src/parking_lot.cpp");
        var header = Read("include/dagflow/detail/parking_lot.hpp");
        var waiter = Definition(header, "struct Waiter {")
            .Replace("WaitCondition", "UntimedCV", StringComparison.Ordinal);
        var domain = Definition(header, "struct Domain {");
        var prefix = """
                     #include "parking_fixture.hpp"
                     class ParkingLot {
                      public:
                     WAITER;
                     DOMAIN;
                       std::array<uint32_t, 1> members_{0};
                       OneEntry<Waiter> waiters_;
                       OneEntry<Domain> domains_;
                       OneEntry<std::atomic<uint64_t>> idle_;
                       ParkingLot() {
                         waiters_[0].word_index = 0;
                         waiters_[0].bit = 1;
                         domains_[0] = {0, 1, 1, 1};
                       }
                       uint64_t prepare(uint32_t) noexcept;
                       void cancel(uint32_t) noexcept;
                       void wait(uint32_t, uint64_t, uint32_t, const std::atomic<bool>&);
                       void signal(uint32_t, bool);
                       void wake_one(uint32_t);
                       void wake_worker(uint32_t);
                       bool wake_word(uint32_t, uint64_t);
                       bool wake_domain(uint32_t);
                     };

                     """.Replace("WAITER", waiter, StringComparison.Ordinal)
            .Replace("DOMAIN", domain, StringComparison.Ordinal);
        string[] methods =
        [
            "uint64_t ParkingLot::prepare(", "void ParkingLot::cancel(",
            "void ParkingLot::wait(", "void ParkingLot::signal(uint32_t worker, bool claimed)",
            "void ParkingLot::wake_one(", "void ParkingLot::wake_worker(",
            "bool ParkingLot::wake_word(", "bool ParkingLot::wake_domain("
        ];
        return Atomics(prefix + string.Join("\n", methods.Select(m => Definition(source, m)
            .Replace("\n#endif\n  auto& waiter", "\n  auto& waiter", StringComparison.Ordinal))));
    }

    private string Completion()
    {
        var source = Read("src/thread_pool.cpp");
        var header = Read("include/dagflow/handle.hpp");
        var credit = Definition(header, "class CompletionCredit {")
            .Replace(" private:", " public:", StringComparison.Ordinal);
        var state = Once(Definition(header, "struct CompletionState {"),
            "static void retire(CompletionState* state) noexcept;",
            "static void retire(CompletionState* state) noexcept;\n"
            + "  static void model_finish_terminal(CompletionState* state) noexcept;");
        string[] methods =
        [
            "void detail::CompletionState::retain(",
            "void detail::CompletionState::release(", "void detail::CompletionState::set_error(",
            "std::exception_ptr detail::CompletionState::get_error(",
            "void detail::CompletionState::add_dependent(",
            "void detail::CompletionState::retire(", "void detail::CompletionState::wait(",
            "void detail::CompletionCredit::fail(", "void detail::CompletionCredit::finish("
        ];
        var bodies = string.Join("\n", methods.Select(m => Definition(source, m)));
        var retire = Definition(source, "void detail::CompletionState::retire(");
        var offset = retire.IndexOf("  CompletionState* pending = state;", StringComparison.Ordinal);
        if (offset < 0) throw new InvalidDataException("Completion retire anchor changed.");
        var replacement = retire[..offset] + "  model_finish_terminal(state);\n}\n"
                                           + "void detail::CompletionState::model_finish_terminal(CompletionState* state) noexcept {\n"
                                           + retire[offset..];
        bodies = Once(bodies, retire, replacement);
        var edge = Definition(retire, "for (auto& edge : edges)");
        var edgeBody = edge[(edge.IndexOf('{') + 1)..^1];
        bodies += "\nvoid model_deliver_edge(CompletionCredit edge) {\n"
                  + "  CompletionState* pending = nullptr;\n  int failure = 0;\n"
                  + edgeBody + "\n  if (pending) CompletionState::model_finish_terminal(pending);\n}\n";
        var ready = Once(Definition(header, "[[nodiscard]] bool ready() const noexcept"),
            "[[nodiscard]] bool ready() const noexcept",
            "bool model_ready(const CompletionState* state_) noexcept");
        bodies += "\n" + ready;
        bodies = bodies.Replace("detail::CompletionState", "CompletionState", StringComparison.Ordinal)
            .Replace("detail::CompletionCredit", "CompletionCredit", StringComparison.Ordinal);
        bodies = Regex.Replace(bodies, @"\berror\b", "error($)")
            .Replace("int failure;", "int failure{};", StringComparison.Ordinal);
        var result = """
                     #include "model_support.hpp"
                     #include <memory>
                     #include <utility>
                     #include <vector>
                     #include <dagflow/detail/fuzz_points.hpp>
                     class Handle;
                     struct CompletionState;
                     struct CompletionWaiter {
                       struct Parking { void wake_worker(unsigned) { RL_ASSERT(false); } } parking;
                       unsigned worker{};
                       CompletionWaiter* next{};
                     };
                     template<class T> using RuntimeAllocator = std::allocator<T>;
                     template<class T> struct ObjectDeleter { void operator()(T* state) const; };

                     """ + credit + ";\n" + state + ";\n\n" + """
                                                              inline std::ostream& operator<<(std::ostream& out, CompletionState::Dependents gate) {
                                                                return out << unsigned(gate);
                                                              }
                                                              template<class T> void ObjectDeleter<T>::operator()(T* state) const {
                                                                RL_ASSERT(state->credits.load(rl::memory_order_relaxed) == 0);
                                                                RL_ASSERT(state->ready.load(rl::memory_order_relaxed));
                                                                RL_ASSERT(!state->payload && state->dependents.empty() && !state->worker_waiters);
                                                                // Storage remains pinned in the fixture; deallocation/reclamation is not modeled.
                                                              }
                                                              inline CompletionState::~CompletionState() = default;

                                                              """ + bodies;
        result = result.Replace("std::exception_ptr", "int", StringComparison.Ordinal)
            .Replace("int failure;", "int failure{};", StringComparison.Ordinal);
        result = Once(result, "int error;", "rl::var<int> error;")
            .Replace("int error($)", "int error", StringComparison.Ordinal)
            .Replace("state_->set_error(std::move(error($)))", "state_->set_error(std::move(error))",
                StringComparison.Ordinal);
        return Atomics(result);
    }

    private string IdleAccounting()
    {
        var source = Read("src/idle_accounting.cpp");
        var header = Read("include/dagflow/detail/idle_accounting.hpp");
        var state = Definition(header, "class IdleAccounting {")
            .Replace(" private:", " public:", StringComparison.Ordinal);
        state = Once(state, "explicit IdleAccounting(uint32_t workers);",
            "explicit IdleAccounting(uint32_t workers) : identity_(1), workers_(workers) {}");
        state = Once(state, "~IdleAccounting();", "~IdleAccounting() = default;");
        state = Once(state, "Lane& producer_lane();",
            "Lane& producer_lane() { RL_ASSERT(producers_); return producers_->lane; }");
        string[] methods =
        [
            "void IdleAccounting::publication_exhausted(",
            "void IdleAccounting::publish_external(", "bool IdleAccounting::idle_locked(",
            "void IdleAccounting::worker_idle(", "void IdleAccounting::wait("
        ];
        var result = "#include \"idle_accounting_fixture.hpp\"\n"
                     + Definition(source, "struct Total {") + ";\n" + state + ";\n"
                     + string.Join("\n", methods.Select(m => Definition(source, m)));
        return Atomics(result).Replace("std::condition_variable", "rl::condition_variable", StringComparison.Ordinal);
    }

    private string Shutdown()
    {
        var source = Read("src/thread_pool.cpp");
        var header = Read("include/dagflow/thread_pool.hpp");
        var guard = Definition(header, "class PublicationGuard {");
        var start = header.IndexOf("  static constexpr uint64_t admission_closed_bit", StringComparison.Ordinal);
        var end = header.IndexOf("\n};", start, StringComparison.Ordinal);
        if (start < 0 || end < 0) throw new InvalidDataException("Shutdown field anchor changed.");
        var fields = header[start..end];
        var prefix = """

                     #include "shutdown_fixture.hpp"
                     class Pool {
                      public:
                     GUARD;
                     FIELDS
                       IdleAccounting* accounting_;
                       ModelParking* parking_;
                       std::array<ModelThread, 0> threads_;
                       // All modeled callers of guard/shutdown are external. Workers publish
                       // descendants directly, under their live parent accounting obligation.
                       static constexpr Pool* tls_pool_ = nullptr;
                       Pool(IdleAccounting& accounting, ModelParking& parking)
                           : accounting_(&accounting), parking_(&parking) {}
                       void close() noexcept;
                       bool closed() const noexcept;
                       void enter_publication();
                       void leave_publication() noexcept;
                       void shutdown();
                       void wait_idle();
                     };

                     """.Replace("GUARD", guard, StringComparison.Ordinal)
            .Replace("FIELDS", fields, StringComparison.Ordinal);
        string[] methods =
        [
            "void Pool::close(", "bool Pool::closed(",
            "Pool::PublicationGuard::PublicationGuard(", "Pool::PublicationGuard::~PublicationGuard(",
            "void Pool::enter_publication(", "void Pool::leave_publication(",
            "void Pool::shutdown(", "void Pool::wait_idle("
        ];
        return IdleAccounting() + Atomics(prefix + string.Join("\n", methods.Select(m => Definition(source, m))));
    }
}