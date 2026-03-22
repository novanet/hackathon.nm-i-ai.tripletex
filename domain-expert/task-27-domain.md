# Task 27 — Domain Breakdown: Foreign Currency Payment with Disagio / Agio

## Original Prompt

> Nous avons envoyé une facture de 6893 EUR à Rivière SARL (nº org. 909090121) lorsque le taux de change était de 10.37 NOK/EUR. Le client a maintenant payé, mais le taux est de 9.87 NOK/EUR. Enregistrez le paiement et comptabilisez l'écart de change (disagio) sur le bon compte.

## English Translation

> We sent an invoice of EUR 6,893 to Rivière SARL (org. no. 909090121) when the exchange rate was 10.37 NOK/EUR. The customer has now paid, but the rate is 9.87 NOK/EUR. Record the payment and post the exchange rate difference (disagio) to the correct account.

## Scenario

Foreign currency invoice settled at a different exchange rate than at invoice time — resulting in either a realized **currency loss (disagio)** or a realized **currency gain (agio)**.

### How to determine disagio vs. agio

- **Payment rate < Invoice rate** → we receive fewer NOK than booked → **Disagio** (loss) → account **8160**
- **Payment rate > Invoice rate** → we receive more NOK than booked → **Agio** (gain) → account **8060**

---

## Original Invoice (already booked — same for both cases)

| Item                | EUR        | Rate         | NOK                    |
| ------------------- | ---------- | ------------ | ---------------------- |
| Invoice to customer | EUR amount | Invoice rate | **EUR × Invoice rate** |

### Journal entry at invoice date

| Account                                      | Debit (NOK)        | Credit (NOK)       |
| -------------------------------------------- | ------------------ | ------------------ |
| 1500 — Accounts Receivable (Kundefordringer) | EUR × Invoice rate |                    |
| 3000 — Sales Revenue                         |                    | EUR × Invoice rate |

---

## Case 1: Disagio (Currency Loss) — Payment rate < Invoice rate

### Example: Rivière SARL — 6,893 EUR, invoice rate 10.37, payment rate 9.87

| Item    | EUR      | Rate  | NOK           |
| ------- | -------- | ----- | ------------- |
| Invoice | 6,893.00 | 10.37 | **71,480.41** |
| Payment | 6,893.00 | 9.87  | **68,033.91** |

**Exchange rate difference:** 71,480.41 − 68,033.91 = **3,446.50 NOK loss**

### Journal Entry — Payment + Disagio

| Account                                        | Debit (NOK)  | Credit (NOK) | Description                |
| ---------------------------------------------- | ------------ | ------------ | -------------------------- |
| 1920 — Bank (Bankkonto)                        | 68,033.91    |              | Cash received at spot rate |
| **8160 — Currency Loss (Valutatap / Disagio)** | **3,446.50** |              | Realized FX loss           |
| 1500 — Accounts Receivable                     |              | 71,480.41    | Clears the receivable      |

---

## Case 2: Agio (Currency Gain) — Payment rate > Invoice rate

### Example: Ironbridge Ltd — 11,671 EUR, invoice rate 11.22, payment rate 11.71

| Item    | EUR       | Rate  | NOK            |
| ------- | --------- | ----- | -------------- |
| Invoice | 11,671.00 | 11.22 | **130,948.62** |
| Payment | 11,671.00 | 11.71 | **136,667.41** |

**Exchange rate difference:** 136,667.41 − 130,948.62 = **5,718.79 NOK gain**

### Journal Entry — Payment + Agio

| Account                                         | Debit (NOK) | Credit (NOK) | Description                |
| ----------------------------------------------- | ----------- | ------------ | -------------------------- |
| 1920 — Bank (Bankkonto)                         | 136,667.41  |              | Cash received at spot rate |
| 1500 — Accounts Receivable                      |             | 130,948.62   | Clears the receivable      |
| **8060 — Currency Gain (Valutagevinst / Agio)** |             | **5,718.79** | Realized FX gain           |

---

## Key Accounting Concepts

- **Disagio**: A realized currency exchange loss — the NOK value received is _less_ than the NOK value booked at invoice time because the foreign currency weakened against NOK. Posted to **8160 — Valutatap** (debit, increases financial expense).
- **Agio**: A realized currency exchange gain — the NOK value received is _more_ than the NOK value booked at invoice time because the foreign currency strengthened against NOK. Posted to **8060 — Valutagevinst** (credit, increases financial income).
- **Account 8160** (NS 4102): "Valutatap" — P&L account for realized foreign exchange losses (financial expense).
- **Account 8060** (NS 4102): "Valutagevinst" — P&L account for realized foreign exchange gains (financial income).
- The **Accounts Receivable** ledger for the customer is fully settled in both cases — the original NOK amount is always credited out in full.
- The **bank account** always reflects the actual NOK received at the payment-date spot rate.
- The **difference** between invoice NOK and payment NOK hits the income statement as either a financial expense (8160) or financial income (8060).

## Verification Checklist

For any FX payment prompt, verify:

1. **Debits = Credits** (the entry must balance)
2. **AR is fully cleared** at the original invoice NOK amount
3. **Bank** reflects actual NOK received (EUR × payment rate)
4. **Correct account**: 8160 for loss, 8060 for gain
5. **Correct side**: 8160 is debited (expense up), 8060 is credited (income up)

## Summary

Both cases are three-line compound journal entries. The only difference is whether the FX difference is debited to 8160 (disagio/loss) or credited to 8060 (agio/gain). The customer's AR is always cleared at the original booked amount, and the bank always reflects actual cash received.
