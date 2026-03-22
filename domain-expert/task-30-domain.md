# Forenkla årsoppgjer 2025 — Fagleg nedbrytning

## Oversikt

Oppgåva er eit **forenkla årsoppgjer** (simplified year-end closing) med tre delar:

1. Rekne ut og bokføre årlege avskrivingar for tre eigedelar (kvar som eige bilag)
2. Reversere forskotsbetalt kostnad
3. Rekne ut og bokføre skattekostnad (22 %)

---

## 1. Årlege avskrivingar (Depreciation)

**Metode:** Lineær avskriving (straight-line depreciation)

**Formel:**

```
Årleg avskriving = Kostpris ÷ Levetid
```

(Ingen restverdi er oppgitt, så restverdi = 0)

### Utrekning

| Eigedel | Konto (eigedel) | Kostpris (kr) | Levetid (år) | Årleg avskriving (kr) |
|---|---|---|---|---|
| Programvare (Software) | 1250 | 307 350 | 10 | **30 735,00** |
| IT-utstyr (IT equipment) | 1210 | 475 300 | 9 | **52 811,11** |
| Kjøretøy (Vehicle) | 1230 | 284 700 | 3 | **94 900,00** |

**Sum avskrivingskostnad: 178 446,11 kr**

> **Merknad om avrunding:** 475 300 ÷ 9 = 52 811,11 kr. Avklar avrundingspolicy — nokre føretak rundar til heile kroner (52 811 eller 52 812).

### Bilagsføring (kvar avskriving = eige bilag)

| Bilag | Debet | Kredit | Beløp (kr) | Tekst |
|---|---|---|---|---|
| V1 | 6010 Avskrivingskostnad | 1209 Akk. avskrivingar | 30 735,00 | Avskriving — Programvare 2025 |
| V2 | 6010 Avskrivingskostnad | 1209 Akk. avskrivingar | 52 811,11 | Avskriving — IT-utstyr 2025 |
| V3 | 6010 Avskrivingskostnad | 1209 Akk. avskrivingar | 94 900,00 | Avskriving — Kjøretøy 2025 |

**Rekneskapslogikk:**
- **Debet 6010** aukar kostnaden i resultatrekneskapet (expense ↑)
- **Kredit 1209** aukar motposten (contra-asset) i balansen, som reduserer bokført verdi av anleggsmidle

---

## 2. Reversering av forskotsbetalt kostnad (Prepaid Expense)

Heile saldoen på konto 1700 (Forskotsbetalt kostnad) på **74 650 kr** skal kostnadsførast i 2025.

### Bilagsføring

| Bilag | Debet | Kredit | Beløp (kr) | Tekst |
|---|---|---|---|---|
| V4 | *Relevant kostnadskonto*¹ | 1700 Forskotsbetalt kostnad | 74 650,00 | Periodisering forskotsbetaling 2025 |

**Rekneskapslogikk:**
- **Debet [kostnadskonto]** — flyttar beløpet frå balansepost til resultatrekneskap (expense ↑)
- **Kredit 1700** — reduserer eigedelen i balansen (asset ↓)

> ¹ **Avklaring nødvendig:** Oppdraget spesifiserer ikkje kva kostnadskonto dei 74 650 kr gjeld. Vanlege kandidatar:
> - **6300** Leigekostnad (rent)
> - **6400** Forsikring (insurance)
> - **6340** Leasing
> - Fordeling på fleire kontoar dersom forskotsbetaling dekker ulike kategoriar

---

## 3. Skattekostnad (Income Tax Expense)

**Skattesats:** 22 % av skattbart resultat

### Formel

```
Skattekostnad = Skattbart resultat × 0,22
```

Der *skattbart resultat* = totale inntekter − totale kostnader (inkludert 178 446 kr i avskrivingar og 74 650 kr i periodisert kostnad frå steg 1–2).

### Bilagsføring

| Bilag | Debet | Kredit | Beløp (kr) | Tekst |
|---|---|---|---|---|
| V5 | 8700 Skattekostnad | 2920 Betalbar skatt | *Utrekna* | Skattekostnad 2025 |

**Rekneskapslogikk:**
- **Debet 8700** — kostnad i resultatrekneskapet (expense ↑)
- **Kredit 2920** — kortsiktig gjeld i balansen (liability ↑)

> **Avklaring nødvendig:** Treng saldobalanse eller skattbart resultat for å kalkulere eksakt beløp.

---

## Rekkjefølgje (Sequencing)

| Steg | Handling | Grunngjeving |
|---|---|---|
| 1 | Bokfør V1–V3 (avskrivingar) | Påverkar kostnader i resultatrekneskapet |
| 2 | Bokfør V4 (forskotsbetaling) | Påverkar kostnader i resultatrekneskapet |
| 3 | Rekn ut skattbart resultat | Basert på oppdatert saldobalanse etter steg 1–2 |
| 4 | Bokfør V5 (skatt) | Siste postering — avheng av netto resultat |

---

## Fagterminologi (Glossary)

| Norsk | Engelsk | Forklaring |
|---|---|---|
| Lineær avskriving | Straight-line depreciation | Lik årleg nedskriving over levetida til eigedelen |
| Akkumulerte avskrivingar (1209) | Accumulated depreciation | Motpost (contra-asset) som reduserer bokført verdi |
| Avskrivingskostnad (6010) | Depreciation expense | Periodekostnad i resultatrekneskapet |
| Forskotsbetalt kostnad (1700) | Prepaid expense | Betalt på førehand, kostnadsført når forbrukt |
| Skattbart resultat | Taxable income | Resultat før skatt per resultatrekneskap |
| Betalbar skatt (2920) | Tax payable | Gjeld til skattemyndigheiter |
| Bilag | Voucher / journal entry | Nummerert dokumentasjon for kvar postering |

---

## Kvalitetsvurdering

**Tryggleiksgrad: 9/10** — Kryss-sjekka mot [Accountingverse.com](https://www.accountingverse.com/accounting-basics/).

| Element | Accountingverse stadfestar | Vår tilnærming | Samsvar |
|---|---|---|---|
| Lineær avskriving | (Kostpris − Restverdi) ÷ Levetid | Kostpris ÷ Levetid (restverdi = 0) | ✅ |
| Avskrivingspostering | Dr Depreciation Expense, Cr Accumulated Depreciation | Debet 6010, Kredit 1209 | ✅ |
| Forskotsbetaling (asset-metode) | Dr Expense, Cr Prepaid Expense | Debet [kostnad], Kredit 1700 | ✅ |
| Skatteperiodisering | Dr Expense, Cr Liability | Debet 8700, Kredit 2920 | ✅ |
| Rekkjefølgje | Justeringspostarar før rekneskapsavslutting | Avskrivingar & forskotsbetaling først, skatt sist | ✅ |

**−1 poeng:** Kostnadskonto for forskotsbetaling (V4) er ikkje spesifisert i oppdraget og krev avklaring.
