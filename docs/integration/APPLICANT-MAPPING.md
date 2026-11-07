# Applicant mapping: FFPJM

Verified read-only against JMDB01/FFPJM as FFTPJ\svc_ffpbridge using Kerberos on 2026-11-02. No FFPJM DDL is required or included.

| Persisted Quick Intake field | Destination | Verified SQL definition |
| --- | --- | --- |
| FirstName | ApplicationLetters.FirstName | nvarchar(200) NULL |
| LastName | ApplicationLetters.LastName | nvarchar(100) NULL |
| StreetAddress | ApplicationLetters.Street | nvarchar(500) NULL |
| CommunityDistrict | ApplicationLetters.Town | nvarchar(500) NULL |
| Parish | Existing parish lookup and reference-number generator | bigint NULL |
| DateOfBirth | ApplicationLetters.DateOfBirth, calendar date at midnight | datetime NULL |
| Gender | ApplicationLetters.Gender | int NULL; committed FFP enum Unspecified=0, Male=1, Female=2 |
| MobileNumber (UI Telephone) | PhoneNumbers.Number and ApplicationLetters.MainContactNo | nvarchar(15) NULL / nvarchar(255) NULL |
| SecondaryTelephone | Second PhoneNumbers.Number when nonblank and different | nvarchar(15) NULL |
| Email | EmailAddresses.MailboxName | nvarchar(100) NULL |
| EmploymentStatus | ApplicationLetters.Occupation | nvarchar(255) NULL; existing mapping preserved |

PhoneNumbers.OID is the int identity primary key. PhoneNumbers.ApplicationLetter is a bigint FK to ApplicationLetters.ID. NumberType 93 means Applicant; 83 means Secondary Number. Network 5 means No Carrier Assigned. EmailAddresses.ApplicationLetter has the same ApplicationLetters.ID relationship; AddressType 93 labels the applicant email. The unrelated singular PhoneNumber table is never used.

The Portal normalizes explicit applicant telephone inputs to E.164 (JM default region); the bridge persists canonical digits only and enforces the 15-character limit, rejects extensions/unsupported characters, deduplicates equivalent primary/secondary numbers, and omits blank inputs. It does not infer carriers. Name/ContactPerson, regular organization phone/address behavior, existing parish logic and SQL target guards are preserved. Regular submissions without explicit applicant fields create no applicant phone/email children.

Reference metadata is checked inside the serializable transaction before allocating a reference or inserting a letter. Children are inserted after the new letter ID is obtained, in that same transaction. Existing synchronized letters return before reference-metadata preparation or child insertion, so retries do not backfill or alter historical contacts. There is no new uniqueness constraint: existing production duplicate phone rows are preserved.

## Required deployment decision: ambiguous line types

Phone classification uses libphonenumber-csharp, with SQL lookup resolution by verified Name/Description. FFPJM PhoneLineTypes ID 1 has Name `4`, Description `Land Line`; ID 2 is Mobile / Cellular Mobile; ID 3 is Skype / Internet Telephony. Neither every primary number nor every persisted MobileNumber is assumed mobile.

The library classifies the Jamaican fixed-line example as FIXED_LINE_OR_MOBILE. There is no verified Unknown/Unspecified LineType lookup in FFPJM. Missing or ambiguous mappings are rejected before any new-letter/reference-counter writes. A verified existing neutral lookup or an explicitly approved line-type capture/policy is required before production rollout. The code does not create lookup rows or use arbitrary/null required line types. Production deployment and acceptance are held until that decision is resolved.

Email over 100 characters and DOB before SQL datetime's 1753 lower bound are rejected, never truncated. Unrecognized gender strings retain their Comments representation and do not acquire an invented numeric enum.

## Portal migration and verification

The separately approved existing PostgreSQL migration `20261014190000_AddFfpManagerApplicationNumber` adds only nullable text `integration_links.ApplicationNumber` and its migration-history entry. An isolated PostgreSQL test compares columns, indexes and constraints before and after that migration. The production migration remains unapplied while the LineType blocker remains unresolved.

Automated coverage includes persisted JSON transfer, independent Street/Town, DOB/Gender/Email/employment contract values, primary-only/two-number/equal-number/blank-secondary cases, line-type ambiguity, SQL lengths, regular organization/individual requests and Portal retry identity. Optional rehearsal SQL cases verify direct child readback and unchanged child count after retry; these require explicit opt-in and are not evidence of an executed production acceptance test.

## Quick Intake additional field mappings — verified 2026-11-07

A read-only metadata query authenticated as SQL login `Monday` directly verified
JMDB01 / FFPJM and these existing `dbo.ApplicationLetters` columns. No SQL writes
or schema changes were performed. The metadata query returned the column results;
the batch returned a nonzero status because this login lacks VIEW SERVER STATE for
the separate session-encryption check. Encryption state was not verified.

| Source | Destination | Verified SQL definition / mapping |
| --- | --- | --- |
| Weekly income, retaining the legacy `MonthlyIncome` source member/column name | Income | decimal(18,10) NULL; unchanged JMD amount, at most two decimal places; reject values above 99,999,999.99 |
| Weekly income period | PayFrequency | int NULL; 2 (Weekly) for Quick Intake |
| MaritalStatus | MaritalStatus | int NULL; Single=0, Married=1, Divorced=2, Widowed=3; case/outer spaces ignored; unknown=NULL |
| HasChildrenUnder18LivingFullTime | Dependents | nvarchar(255) NULL; true=`One or More`, false=`None`, unknown=NULL; no numeric count inferred |
| HasDisabilityOrSeriousMedicalCondition | Disabled | bit NULL; true/false/NULL |
| Selected Current Assistance programmes | Programme | nvarchar(255) NULL; all selected labels, deduplicated, existing capture-option ordering, comma-space separated |
| Selected victim circumstances | Circumstance | nvarchar(255) NULL; correctly spelled column; all labels, deduplicated, existing capture-option ordering, comma-space separated |

The repository owner explicitly confirmed that historical amounts were weekly but
mislabeled MonthlyIncome. Existing values are used unchanged: there is no historical
conversion, reconciliation or database migration. Both Quick Intake POST handlers
now pass the entered weekly amount unchanged rather than converting it to monthly.
The legacy member/column and form binding names remain for compatibility; no form
markup or income-dashboard behavior is changed by this integration correction.

The existing paired migration sentinel (DOB 1900-01-01 and street `Not captured`)
identifies uncaptured profile fields. Its default zero/false/Single/None values are
not actual answers and transfer as NULL. A genuine captured zero remains zero.

Implemented Current Assistance options remain PATH, Poor Relief Dole,
Rehabilitation Programme, Social Pension, CARE, and None / None of the Above.
The existing ProfileIntakeChoices helper maps the historical Solidarity Program
alias to Social Pension; both capture and transfer reuse that rule.
Examples in requirements do not create new form options. The explicit None aliases
transfer as `None of the Above`; missing or empty answers remain NULL. None cannot
be combined with another choice. Programme never falls back to requested assistance,
HelpCategory, Category or ProgramCategory for a Quick Intake. ItemsRequested is unchanged.
The separately named MonthlyHouseholdIncome profile field continues to receive a
derived monthly equivalent; existing profile values are not overwritten.

The actual victim labels are supplied by QuickIntakeVictimCircumstances.Options;
commas inside one label remain part of that label. Neither field is silently truncated.

New transfer members are optional, so older JSON messages still deserialize.
Quick Intake is identified by its existing DocumentSource marker; absent old fields
remain NULL and do not fall back to ProgramCategory. Regular submissions retain
Programme=ProgramCategory and their existing insert columns/defaults. Both SQL gateways
use the same parameter mapping only on the existing new-application insertion path;
existing synchronized applications still return before any insertion or update.
