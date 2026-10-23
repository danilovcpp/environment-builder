# environment-builder

## GitHub Spec Kit

В проекте настроен Spec Kit 1.2.3 для Codex со скриптами PowerShell.
Навыки находятся в `.agents/skills/`, шаблоны и workflow — в `.specify/`.

Запускайте навыки в чате Codex из папки проекта:

1. `$speckit-constitution` — принципы проекта.
2. `$speckit-specify` — спецификация функциональности.
3. `$speckit-plan` — план реализации.
4. `$speckit-tasks` — список задач.
5. `$speckit-implement` — реализация.

Локальный CLI установлен в `.tools/spec-kit/` и исключён из Git:

```powershell
.\.tools\spec-kit\Scripts\specify.exe version
.\.tools\spec-kit\Scripts\specify.exe check
```

Для восстановления CLI после клонирования (Python 3.11+):

```powershell
python -m venv .tools/spec-kit
.\.tools\spec-kit\Scripts\python.exe -m pip install specify-cli==1.2.3
```

Документация: [GitHub Spec Kit](https://github.com/github/spec-kit).
