# Native process lifecycle and checkpoints

Epoch 8 applies to fresh opt-in binary workspaces. A process operation still binds the admitted request, exact input snapshot, and results reservation. The runner writes the canonical process checkpoint with an increasing generation and the event cursor in one SQLite transaction. The existing public workflow event projection uses the shared append-only event tables.

The dispatch transaction binds the exact checkpoint frame SHA-256 and inserts one durable launch intent per node attempt. That commit must precede OS launch. A worker then appends observations of the supervised child. Durable process identity requires the PID and OS start time together. Reported termination confirms supervised child exit following a cleanup request. It cannot establish quiescence of independently detached descendants.

A crash between OS launch and recording the child start leaves an intent with an uncertain outcome. Recovery preserves the explicit reconciliation and one-shot resume rules. A node with admitted launch intent cannot become eligible for replay. A checkpointed successful node must have a matching durable successful exit observation before continuation.

The terminal process frame lives in an immutable ledger row and commits with finalization. Its exact SHA-256 identifies the native frame. The existing report SHA-256 identifies the JSON projection, which is also available as a virtual artifact. Historical workspaces retain their original JSON checkpoints, reports, journal replay and digests.

## Exact retry at the terminal boundary

| Last durable evidence | Behavior after a stopped Host |
| --- | --- |
| Running checkpoint with no confirmed dispatch | The retry does not launch a child. The original dispatch lacks a bound checkpoint. |
| Launch intent with no start observation | The child may have been created. Retry refuses dispatch and retains the uncertain attempt. |
| Child start or PID and start time without an exit observation | Retry refuses dispatch and never sends a signal to the saved PID. A surviving child requires external reconciliation. |
| Exit observed without a committed node outcome | Retry cannot promote the individual exit observation to a completed node or repeat the attempted node. |
| A successful node committed, with a never-admitted successor | Explicit reconciliation and the one-shot resume claim may admit the successor. It cannot repeat the completed node. |
| The whole terminal checkpoint and final event committed | An identical request can finish the original ledger transition using the retained input, snapshot digest, attempt evidence and reservation. It performs no new execution. |
| Terminal transition committed | Exact retry returns the existing operation and outcome. |

Cancellation is checked before OS launch and supervised through child exit. The interval between the last cancellation check and OS process creation still exists. The normal live executor cleans up the supervised process or records uncertainty. On Linux a stopped Host cannot establish quiescence of arbitrary detached descendants. Epoch 9 retains the Linux boot ID and kernel start counter in the same transaction as the PID observation. The wall-clock start timestamp remains diagnostic. The tests kill and clean up their own deliberately surviving child using the PID and independently verified kernel token.
