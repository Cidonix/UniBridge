# Resource IO budgets qualification

Portable runner links the complete candidate ResourceTools/ResourceBudgetIO/parameters/URI helper and the production Response/ActionOutputSchema. It uses real filesystem bytes, whole-file hash comparisons, an in-flight CTS search, a 30 ms bounded regex and positive/error/partial closed-schema validation. Unity attributes/registry/ambient token and the serializer are explicit seams; resource DTOs do not use Unity converters. This is portable evidence, not an Editor qualification.

Run from the repository root in an Administrator process, with a fresh evidence directory:

```powershell
python '.\com.cidonix.unibridge\Tools~\ResourceBudgetRegression\run_resource_budget_regression.py' --source '.' --output '.\Temp\ResourceBudgetRegression\fresh-run'
```

The published-source mode needs no temporary candidate files. It records every linked source file SHA before and after the run and fails qualification if any changed. To test an external overlay before delivery, add `--candidate <overlay-root>`; that mode additionally requires the overlay's `base-sha256.json` and verifies every exact base hash. Unchanged helper files absent from the overlay are linked from source.

After root promotes and installs the candidate, copy only LiveRegression.cs and LiveRegression.asmdef into a fresh, uniquely owned Assets/Editor subfolder of the already running elevated test Editor. Invoke `Tools/UniBridge/Qualification/Remaining item 2 Resource budgets`, or `UniBridgeResourceBudgetLiveRegression.Run()` through the existing Editor menu/run bridge. The fixture creates only a unique OS Temp root and emits its report path in the Console. It neither saves/reloads scenes nor imports its test files. Read `native-result.json`, require `passed=true`, six checks, actual version and pointerSize8. Remove only the owned fixture folder by guarded owned hashes after the report is recorded. No Editor launches are part of this runner.

Expected negative tool results (`RESOURCE_FILE_BYTES`, `RESOURCE_RETURN_BYTES`, `RESOURCE_INVALID_REGEX`, `RESOURCE_REGEX_TIMEOUT`, `RESOURCE_ENTRY_LIMIT`, `RESOURCE_CANCELLED`, `RESOURCE_TIMEOUT`, `RESOURCE_WORK_LIMIT`) are intentional errors. MCP transport smoke must allow `isError` for those cases. A configured result cap preserves success and exposes `complete=false`, reason `result_limit`. ListResources retains the existing extra `unity://spec/script-edits` URI outside its file-result cap: one matching file with Limit1 therefore produces Count2.

For ordinary MCP wire qualification, create a fresh owned Test Assets folder with a short text file and one bounded large file; invoke ReadResource/FindInFile/ListResources with that Assets URI/Under and the caps above. Confirm the scheduler's explicit cancellation independently while a long line-by-line search is active. The portable suite proves helper and ambient-token cooperation; the native menu proves Mono/Editor execution; neither alone asserts MCP transport cancellation.

Limits are cooperative. Checks surround bounded reads, lazy enumerator steps and timed regex matches; the OS may stall an individual filesystem call. Default16MiB scan/256KiB return/20k entries/1M work/3s observation/50ms match; hard64MiB/2MiB/100k/8M/10s/250ms. SHA is nullable unless the complete stable file was scanned, even when returned text is partial. Tail requires EOF. Legacy standalone adapters currently do not expose these tools, so this candidate does not claim a new legacy tool surface.
