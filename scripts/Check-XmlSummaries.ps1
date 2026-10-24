[CmdletBinding()]
param(
    [string]$Root
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = Join-Path $PSScriptRoot '..'
}
$resolvedRoot = (Resolve-Path -LiteralPath $Root).Path
$sourceRoots = @(
    (Join-Path $resolvedRoot 'src'),
    (Join-Path $resolvedRoot 'tests')
)
$sourceExtensions = @('.cs', '.cpp', '.h', '.hpp', '.inc', '.cc', '.cxx')
$hangulPattern = '[가-힣]'
$summaryCount = 0
$issues = [System.Collections.Generic.List[string]]::new()
$rootPrefix = $resolvedRoot.TrimEnd([char[]]@([char]92, [char]47)) + [System.IO.Path]::DirectorySeparatorChar

foreach ($sourceRoot in $sourceRoots) {
    if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
        continue
    }

    $sourceFiles = Get-ChildItem -LiteralPath $sourceRoot -Recurse -File | Where-Object {
        $sourceExtensions -contains $_.Extension.ToLowerInvariant() -and
        $_.FullName -notmatch '[\\/](obj|bin)[\\/]'
    }

    foreach ($sourceFile in $sourceFiles) {
        $lines = [System.IO.File]::ReadAllLines($sourceFile.FullName, [System.Text.Encoding]::UTF8)
        $insideSummary = $false
        $summaryStartLine = 0
        $summaryText = [System.Text.StringBuilder]::new()

        for ($lineIndex = 0; $lineIndex -lt $lines.Length; $lineIndex++) {
            $line = $lines[$lineIndex]

            if (-not $insideSummary) {
                $opening = [System.Text.RegularExpressions.Regex]::Match(
                    $line,
                    '///\s*<summary\b[^>]*>'
                )
                if (-not $opening.Success) {
                    continue
                }

                $insideSummary = $true
                $summaryStartLine = $lineIndex + 1
                $content = $line.Substring($opening.Index + $opening.Length)
            }
            else {
                if ($line -notmatch '^\s*///') {
                    $relativePath = $sourceFile.FullName.Substring($rootPrefix.Length)
                    $issues.Add("${relativePath}:${summaryStartLine}: <summary> 주석이 /// 문서 주석을 벗어나 닫히지 않았습니다.")
                    $insideSummary = $false
                    $summaryText.Clear() | Out-Null
                    continue
                }

                $content = [System.Text.RegularExpressions.Regex]::Replace($line, '^\s*///\s?', '')
            }

            $closing = [System.Text.RegularExpressions.Regex]::Match($content, '</summary\s*>')
            if ($closing.Success) {
                [void]$summaryText.Append($content.Substring(0, $closing.Index))
                $plainText = [System.Text.RegularExpressions.Regex]::Replace($summaryText.ToString(), '<[^>]+>', ' ')
                $summaryCount++

                if ($plainText -notmatch $hangulPattern) {
                    $relativePath = $sourceFile.FullName.Substring($rootPrefix.Length)
                    $issues.Add("${relativePath}:${summaryStartLine}: XML <summary>에 한글 설명이 없습니다.")
                }

                $insideSummary = $false
                $summaryText.Clear() | Out-Null
                continue
            }

            [void]$summaryText.AppendLine($content)
        }

        if ($insideSummary) {
            $relativePath = $sourceFile.FullName.Substring($rootPrefix.Length)
            $issues.Add("${relativePath}:${summaryStartLine}: <summary> 주석이 닫히지 않았습니다.")
        }
    }
}

if ($issues.Count -gt 0) {
    $details = $issues -join [Environment]::NewLine
    throw "XML summary 언어 검사 실패 ($($issues.Count)건):$([Environment]::NewLine)$details"
}

Write-Output "XML summary 한글 검사 통과: ${summaryCount}개"
