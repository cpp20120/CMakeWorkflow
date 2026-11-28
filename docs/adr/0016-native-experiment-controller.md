# Native experiment controller plans

The experiment CLI explicitly selects schema 4 with `experiment plan --native-controller`. Schema 4 publishes a canonical Cap’n Proto plan alongside the source ZIP. A distinct approval domain binds the exact plan bytes. The plan identifies the retained workflow by document, blob and canonical digest and also binds the source snapshot, ZIP, selected Host, placement and optional dependency plan. The workflow descriptor binds the environment policy carried in the workflow. The controller verifies the frame and reopens the verified workflow before contacting the Host.

The existing pipeline database owns controller approval, dependency approval and receipt identity. Approval commits before any run staging. The existing pending stage can resume upload and publication after an interruption. The existing coordinator owns child request intent, Host lookup, observation and terminal evidence. Dependency and experiment approvals remain independent. The exact public pipeline manifest, Git ZIP, public controller inspection and result export formats remain supported.

Schema 3 remains the format for `experiment plan --binary`. Its exact-byte approval domain is preserved. A schema-4 frame requires approval under its own identity.
