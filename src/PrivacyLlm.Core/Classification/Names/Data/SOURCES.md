# Name detection word lists

Embedded in `PrivacyLlm.Core` and loaded by `NameLexicon`. The app refuses to start if a list is missing or
below its minimum size.

| File | Contents |
|---|---|
| `given_names.txt` | Indian given names across regions and communities, lowercase, with common romanisation variants |
| `surnames.txt` | Indian surnames and family/clan names |
| `common_words.txt` | Everyday English and romanised Hindi/Hinglish words, including commerce and support vocabulary |
| `ambiguous.txt` | `name\|reason` for names that are also ordinary words, deities, places or brands; these need a context cue |
| `exclusion_phrases.txt` | Multi-word places, festivals, institutions, brands and religious objects whose name-like words are not people |
| `places.txt` | Indian states, cities, towns, localities, rivers and pilgrimage sites (plus a few neighbouring-country cities) |

## Provenance

All lists were written from general knowledge of common names and words and reviewed in this repository.
They contain names and words only: no person's name is linked to any other data, and there are no full names
of real individuals.

Not used, and not to be used: electoral rolls, leaked or scraped personal datasets, customer data, or any
list that ties a name to a real person.

## Editing rules

- Lowercase, one entry per line, sorted, no duplicates.
- A word that is both a name and an everyday word belongs in `common_words.txt` (and is then treated as
  ambiguous automatically), or in `ambiguous.txt` with a reason.
- After editing, run the name evaluation tests. `name-eval-holdout-v2.jsonl` is the held-out set: do not tune
  rules or lists against its individual examples.
