param(
    [string]$ToolPath = (Join-Path $PSScriptRoot '../../publish-aesfinder/AesFinder.exe')
)

$ErrorActionPreference = 'Stop'
$ToolPath = (Resolve-Path -LiteralPath $ToolPath).Path
$testPrefix = Join-Path ([System.IO.Path]::GetTempPath()) ('aesfinder-json-' + [guid]::NewGuid())
$fixturePath = $testPrefix + '.bin'
$emptyPath = $testPrefix + '-empty.bin'
$stderrPath = $testPrefix + '.stderr'

function Invoke-Finder([string[]]$FinderArgs, [int]$ExpectedExit) {
    $stdout = & $ToolPath @FinderArgs 2> $stderrPath
    if ($LASTEXITCODE -ne $ExpectedExit) {
        throw "Expected exit $ExpectedExit, got ${LASTEXITCODE}: $stdout"
    }
    return $stdout -join "`n"
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

try {
    # A synthetic mov-immediate sequence carrying a known 32-byte key, independent of a game install.
    $fixture = [byte[]]::new(4096)
    [array]::Fill[byte]($fixture, 0x90)
    foreach ($offset in @(0, 7, 14, 21, 32, 39, 46, 53)) { $fixture[$offset] = 0xC7 }
    $keyOffsets = @(3, 10, 17, 24, 35, 42, 49, 56)
    for ($word = 0; $word -lt 8; $word++) {
        for ($part = 0; $part -lt 4; $part++) {
            $fixture[$keyOffsets[$word] + $part] = [byte]($word * 4 + $part + 1)
        }
    }
    $expectedKey = '0x' + ((1..32 | ForEach-Object { '{0:X2}' -f $_ }) -join '')
    [System.IO.File]::WriteAllBytes($fixturePath, $fixture)
    [System.IO.File]::WriteAllBytes($emptyPath, [byte[]]::new(4096))

    foreach ($arguments in @(
        ,@($fixturePath, '--json')
        ,@('--json', '--file', $fixturePath, '--no-api')
    )) {
        $result = (Invoke-Finder $arguments 0) | ConvertFrom-Json
        Assert-True ($result.mainKey -ceq $expectedKey) 'JSON mainKey did not match the fixture.'
        Assert-True ($result.candidates -ccontains $expectedKey) 'JSON candidates omitted the known key.'
        foreach ($field in @('version', 'build', 'fullVersion', 'mainKey', 'candidates')) {
            Assert-True ($result.PSObject.Properties.Name -contains $field) "Missing field: $field"
        }
        Assert-True ($result.version -ceq '') 'A binary without metadata should have an empty version.'
    }

    $legacy = Invoke-Finder @($fixturePath) 0
    Assert-True (($legacy -split "`n") -ccontains $expectedKey) 'Plain-text output lost the known key.'

    foreach ($case in @(
        @{ Arguments = @('--json'); Exit = 1 }
        @{ Arguments = @(($fixturePath + '-missing'), '--json'); Exit = 1 }
        @{ Arguments = @($emptyPath, '--json'); Exit = 2 }
        @{ Arguments = @($fixturePath, '--json', '--unknown'); Exit = 1 }
    )) {
        $result = (Invoke-Finder $case.Arguments $case.Exit) | ConvertFrom-Json
        Assert-True ($null -eq $result.mainKey) 'Failure must not return a mainKey.'
        Assert-True ($result.candidates.Count -eq 0) 'Failure must not return candidates.'
        Assert-True (-not [string]::IsNullOrWhiteSpace($result.error)) 'Failure must return a JSON error.'
    }
    'PASS: JSON success/failure, argument order, and plain-text compatibility.'
}
finally {
    # These are three individually named temporary files; no directory removal is needed.
    foreach ($testFile in @($fixturePath, $emptyPath, $stderrPath)) {
        if (Test-Path -LiteralPath $testFile) { Remove-Item -LiteralPath $testFile }
    }
}
exit 0
