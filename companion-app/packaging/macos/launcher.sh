#!/bin/zsh

# Finder can launch an app bundle with a different working directory. Start the
# companion from its own bundle directory so its bundled web interface is found.
cd "$(dirname "$0")"
exec "$(pwd)/AudioChoiceCompanion.bin"
