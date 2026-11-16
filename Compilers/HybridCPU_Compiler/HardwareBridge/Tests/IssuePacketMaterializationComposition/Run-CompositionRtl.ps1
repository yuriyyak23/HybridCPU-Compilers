param(
    [string]$SystemVerilog = (Join-Path $env:TEMP 'cw-issue-packet-materialization-composition\composition.sv'),
    [string]$OutputDirectory = (Join-Path $env:TEMP 'cw-issue-packet-materialization-composition'),
    [string]$MlirPath = (Join-Path $env:TEMP 'cw-issue-packet-materialization-composition\composition.mlir'),
    [string]$HwirPath = (Join-Path $env:TEMP 'cw-issue-packet-materialization-composition\composition.hwir.json'),
    [string]$SourceHashesPath = (Join-Path $env:TEMP 'cw-issue-packet-materialization-composition\source-hashes.txt')
)
$ErrorActionPreference = 'Stop'
$SystemVerilog = (Resolve-Path -LiteralPath $SystemVerilog).Path
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$text = [IO.File]::ReadAllText($SystemVerilog)
$match = [regex]::Match($text, '(?s)module\s+issue_packet_create_materialization_composition\s*\((.*?)\);')
if (-not $match.Success) { throw 'Composition top declaration was not found.' }
$ports = @()
$direction = $null
$width = ''
foreach ($segment in ($match.Groups[1].Value -split ',')) {
    $segment = ($segment -replace '//.*', '').Trim()
    if (-not $segment) { continue }
    if ($segment -match '^(input|output)\s*(\[[^]]+\])?\s*(\w+)\s*$') {
        $direction = $Matches[1]; $width = $Matches[2]; $name = $Matches[3]
    } elseif ($segment -match '^(\w+)\s*$' -and $direction) {
        $name = $Matches[1]
    } else { throw "Could not parse top port segment '$segment'." }
    $ports += [pscustomobject]@{ Direction = $direction; Width = $width; Name = $name }
}
$inputs = @($ports | Where-Object Direction -eq input)
$outputs = @($ports | Where-Object Direction -eq output)
foreach ($required in @('executable_non_scalar_mask','assigned_lane_mask','materializable_lane_mask')) {
    if ($ports.Name -notcontains $required) { throw "Required port $required is missing." }
}
if ($inputs.Count -ne 69) { throw "Expected 69 top inputs, observed $($inputs.Count)." }
$decls = foreach ($port in $ports) {
    $kind = if ($port.Direction -eq 'input') { 'reg' } else { 'wire' }
    "$kind $($port.Width) $($port.Name);"
}
$connections = ($ports | ForEach-Object { ".$($_.Name)($($_.Name))" }) -join ",`n    "
$tb = @"
module tb;
  $($decls -join "`n  ")
  issue_packet_create_materialization_composition dut (
    $connections
  );
  reg [31:0] seed = 32'h5a17c3e9;
  reg [7:0] expected;
  integer i;
  initial begin
    for (i = 0; i < 4096; i = i + 1) begin
      $($inputs | ForEach-Object { '{0} = $urandom(seed);' -f $_.Name } | Out-String)
      #1;
      expected = (assigned_lane_mask & 8'h0f) | (executable_non_scalar_mask & 8'hf0);
      if (materializable_lane_mask !== expected) begin
        `$display("FAIL vector=%0d assigned=%02x executable=%02x actual=%02x expected=%02x", i, assigned_lane_mask, executable_non_scalar_mask, materializable_lane_mask, expected);
        `$fatal(1);
      end
    end
    `$display("PASS composition RTL vectors=4096 seed=5a17c3e9");
    `$finish;
  end
endmodule
"@
$tbPath = Join-Path $OutputDirectory 'composition_tb.sv'
[IO.File]::WriteAllText($tbPath, $tb, [Text.UTF8Encoding]::new($false))
$iverilog = 'C:\Users\Yuriy\Desktop\CircuitWeave ExTool\final\packages\oss-cad-suite-2026-11-10\oss-cad-suite\bin\iverilog.exe'
$vvp = 'C:\Users\Yuriy\Desktop\CircuitWeave ExTool\final\packages\oss-cad-suite-2026-11-10\oss-cad-suite\bin\vvp.exe'
if (-not (Test-Path -LiteralPath $iverilog) -or -not (Test-Path -LiteralPath $vvp)) { throw 'Pinned Icarus tools are unavailable.' }
$ossBin = Split-Path -Parent $iverilog
$ossLib = Join-Path (Split-Path -Parent $ossBin) 'lib'
$env:PATH = "$ossBin;$ossLib;$env:PATH"
$sim = Join-Path $OutputDirectory 'composition_sim.vvp'
& $iverilog -g2012 -s tb -o $sim $SystemVerilog $tbPath
if ($LASTEXITCODE -ne 0) { throw "iverilog failed: $LASTEXITCODE" }
$result = & $vvp $sim
if ($LASTEXITCODE -ne 0) { throw "vvp failed: $LASTEXITCODE" }
$result | Write-Output
if (($result -join "`n") -notmatch 'PASS composition RTL vectors=4096') { throw 'RTL simulation pass marker missing.' }
$receipt = [ordered]@{
    schema = 'circuitweave.issue-packet-create-materialization-composition-rtl-receipt/v1'
    top = 'issue_packet_create_materialization_composition'
    oracle = "materializable_lane_mask == ((assigned_lane_mask & 8'h0f) | (executable_non_scalar_mask & 8'hf0))"
    modelCommit = '1bfe41a49cf11c80fa6c6aee87accb54b78b331f'
    vectors = 4096
    seed = '5a17c3e9'
    simulator = 'Icarus Verilog 14.0 (OSS CAD Suite 2026-11-10)'
    systemVerilogSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $SystemVerilog).Hash.ToLowerInvariant()
    mlirSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $MlirPath).Hash.ToLowerInvariant()
    hwirSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $HwirPath).Hash.ToLowerInvariant()
    sourceHashesSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $SourceHashesPath).Hash.ToLowerInvariant()
    testbenchSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $tbPath).Hash.ToLowerInvariant()
    simulationImageSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $sim).Hash.ToLowerInvariant()
    result = 'PASS'
}
$json = $receipt | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'rtl-receipt.json'), $json + "`n", [Text.UTF8Encoding]::new($false))
