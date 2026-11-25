@echo off
cd /d "%~dp0"
python -c "import lz4" 2>nul || python -m pip install --user lz4
start "" pythonw gd_easier_progression.py
