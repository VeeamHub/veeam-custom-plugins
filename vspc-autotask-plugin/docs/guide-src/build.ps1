# Builds the guide: combine the parts, render once to learn the page numbers, write them
# into the contents, render again, then confirm the printed numbers still agree.
$doc  = $PSScriptRoot
$node = if ($c = Get-Command node -ErrorAction SilentlyContinue) { $c.Source } else { Join-Path $env:LOCALAPPDATA 'node20\node.exe' }

Get-Content "$doc\part1.html","$doc\part2.html","$doc\part3.html","$doc\part4.html","$doc\part5.html","$doc\part6.html" `
  | Set-Content "$doc\guide.html" -Encoding utf8
"combined guide.html ($((Get-Item "$doc\guide.html").Length) bytes)"

& $node --experimental-websocket "$doc\print.cjs" guide.html pass1.pdf
& $node "$doc\tocpages.cjs" "$doc\pass1.pdf" "$doc\guide.html"

& $node --experimental-websocket "$doc\print.cjs" guide.html Autotask-PSA-Integration-Guide.pdf

Copy-Item "$doc\guide.html" "$doc\guide-verify.html" -Force
& $node "$doc\tocpages.cjs" "$doc\Autotask-PSA-Integration-Guide.pdf" "$doc\guide-verify.html"
if ((Get-Content "$doc\guide.html" -Raw) -eq (Get-Content "$doc\guide-verify.html" -Raw)) {
  "PAGE NUMBERS STABLE"
} else {
  "WARNING: contents page numbers shifted between passes"
}
& $node --experimental-websocket "$doc\measure.cjs"
& $node "$doc\pagecheck.cjs" "$doc\Autotask-PSA-Integration-Guide.pdf"
