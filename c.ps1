$ceeFindOutput = ceefind -first -dir $args
if (-not [string]::IsNullOrEmpty($ceeFindOutput)) {
	Set-Location $ceeFindOutput
}
