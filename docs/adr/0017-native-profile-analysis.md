# Native profile analysis for History

Fresh opt-in binary ledgers use epoch 9. Earlier ledgers keep their original readers and History policies. Production workflow capture results retain typed analysis alongside the capture descriptor in the canonical execution checkpoint and terminal report. The optional report field has a versioned analysis contract. Its terminal report blob hash covers the metrics, analysis status and capture provenance.

Linux perf counter parsing and ETW analysis emit typed metric values using the same parsers as public profiling export. The native pipeline binds metrics to the capture, method, unit and optional variant. Analysis failure remains separate from execution success and prevents indexing incomplete evidence. History verifies the terminal document and consumes typed capture analysis. A missing or corrupt terminal document or incompatible typed evidence fails closed on import. Retrying a History claim does not re-run the workload.

Historical report and profiling JSON readers remain supported. The public profile analysis command still generates the compatibility JSON files, and the capture session still publishes its public capture projections. A new native History import does not generate or read those projections. Raw perf traces, ETL and other external profiler formats keep their original bytes.
