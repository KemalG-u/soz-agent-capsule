#!/bin/bash
# soz-kanca.exe derlemesi (Windows'un kendi .NET Framework 4 derleyicisi; ek araç gerekmez).
# Kullanım (urun klasöründen, Git Bash): bash src/Soz.Kanca/derle.sh <çıktı klasörü>
#   Yayın adımında: bash src/Soz.Kanca/derle.sh yayin   (soz.exe ile aynı klasöre; Söz kurulumu soz-kanca.exe'yi soz.exe'nin yanında arar)
set -e
cd "$(dirname "$0")"
CSC=/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe
[ -x "$CSC" ] || { echo "csc.exe bulunamadı: $CSC"; exit 1; }
OUT="${1:-.}"
mkdir -p "$OUT"
# MSYS yol çevirisini kapat: -nologo gibi bayraklar yol sanılmasın
MSYS2_ARG_CONV_EXCL='*' "$CSC" -nologo -optimize+ -target:exe -platform:anycpu -out:"$(cygpath -w "$OUT")\soz-kanca.exe" SozKanca.cs
ls -la "$OUT/soz-kanca.exe"
