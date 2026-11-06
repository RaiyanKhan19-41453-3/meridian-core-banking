// API client — talks to the Core Banking API.
// Dev: Vite proxies /api → API. Prod: nginx proxies /api → api container.
// Access tokens are short-lived; refresh tokens rotate on every use.
const baseUrl = import.meta.env.VITE_API_URL ?? ''
const ACCESS_KEY = 'meridian.access'
const REFRESH_KEY = 'meridian.refresh'

export interface TokenPairPayload {
  accessToken: string
  accessTokenExpiresAtUtc: string
  refreshToken: string
  refreshTokenExpiresAtUtc: string
  email: string
  fullName: string
  role: string
}
export interface MePayload {
  id: string
  email: string
  fullName: string
  role: string
  twoFactorEnabled: boolean
  kycStatus: string
  createdAt: string
}
export interface Account {
  id: string
  accountNumber: string
  type: string
  currency: string
  status: string
  balance: number
  createdAt: string
}
export interface MoneyResult {
  transactionId: string
  reference: string
  type: string
  status: string
  amount: number
  currency: string
  description: string
  postedAtUtc: string
  replayed: boolean
}
export interface AdminLoanRow {
  id: string
  reference: string
  customer: string
  product: string
  principal: number
  annualInterestRate: number
  termMonths: number
  status: string
  creditScore: number
  appliedAt: string
  disbursedAt: string | null
  installmentsPaid: number
  installmentsTotal: number
  outstanding: number
  overdueInstallments: number
}

export interface OverdraftProductTerms {
  productCode: 'ODL-VAR' | 'ODL-SEC'
  currency: string
  annualInterestRate: number
  minLimit: number
  maxLimit: number
  minCreditScore: number
  requiredCollateralRatio: number | null
  secured: boolean
}
export interface OverdraftFacilityDto {
  id: string
  reference: string
  accountId: string
  accountNumber: string
  limit: number
  annualInterestRate: number
  status: string
  creditScore: number
  decisionReason: string
  appliedAt: string
  approvedAt: string | null
  closedAt: string | null
  currentBalance: number
  availableCredit: number
  accruedInterestUncharged: number
  productCode: string
  collateralAccountId: string | null
  collateralAmount: number | null
  collateralAccountNumber: string | null
}
export interface AdminOverdraftRow {
  id: string
  reference: string
  customer: string
  accountNumber: string
  limit: number
  annualInterestRate: number
  status: string
  creditScore: number
  balance: number
  utilized: number
  available: number
  appliedAt: string
  closedAt: string | null
  productCode: string
  collateralAccountNumber: string | null
}

export interface BatchRunDto {
  id: number
  jobType: string
  runDate: string
  status: 'Succeeded' | 'Partial' | 'Failed'
  itemsProcessed: number
  itemsFailed: number
  summary: Record<string, unknown> | null
  errors: Array<Record<string, unknown>> | null
  startedAt: string
  finishedAt: string | null
  replayed: boolean
}
export interface TrialBalanceLine {
  code: string
  name: string
  type: string
  balance: number
  debit: number
  credit: number
}
export interface TrialBalance {
  lines: TrialBalanceLine[]
  totalDebits: number
  totalCredits: number
  balanced: boolean
}

export interface KycStatusPayload {
  kycStatus: 'Pending' | 'Verified' | 'Rejected'
  riskRating: string
  fullLegalName: string
  profileComplete: boolean
  documentCount: number
  rejectionReason: string | null
  verifiedAt: string | null
}
export interface KycProfilePayload {
  fullLegalName: string
  dateOfBirth: string
  nationality: string
  phone: string
  addressLine: string
  city: string
  country: string
  idType: string
  idNumber: string
  employmentStatus: string
  annualIncomeBand: string
  kycStatus: string
  riskRating: string
  rejectionReason: string | null
  verifiedAt: string | null
  updatedAt: string
}
export interface KycProfileInput {
  fullLegalName: string
  dateOfBirth: string
  nationality: string
  phone: string
  addressLine: string
  city: string
  country: string
  idType: 'NationalId' | 'Passport'
  idNumber: string
  employmentStatus: 'Employed' | 'SelfEmployed' | 'BusinessOwner' | 'Student' | 'Retired' | 'Unemployed'
  annualIncomeBand: string
}
export interface KycDocumentMeta {
  id: string
  docType: string
  fileName: string
  contentType: string
  sizeBytes: number
  sha256: string
  uploadedAt: string
}
export interface PendingProfileDto {
  userId: string
  email: string
  fullLegalName: string
  idType: string
  idNumber: string
  nationality: string
  riskRating: string
  createdAt: string
  documentCount: number
}
export interface AmlAlertDto {
  id: string
  alertType: string
  severity: string
  status: string
  userId: string
  userEmail: string
  accountId: string | null
  transactionId: string | null
  details: string
  createdAt: string
}

export interface LoanProductDto {
  code: string
  name: string
  currency: string
  minPrincipal: number
  maxPrincipal: number
  minTermMonths: number
  maxTermMonths: number
  annualInterestRate: number
  originationFee: number
  minCreditScore: number
}
export interface LoanInstallmentDto {
  installmentNo: number
  dueDate: string
  principal: number
  interest: number
  total: number
  status: string
  paidAt: string | null
}
export interface LoanSummaryDto {
  id: string
  reference: string
  productCode: string
  productName: string
  principal: number
  annualInterestRate: number
  termMonths: number
  status: string
  creditScore: number
  purpose: string
  appliedAt: string
  disbursedAt: string | null
  completedAt: string | null
  installmentsPaid: number
  installmentsTotal: number
  totalPaid: number
  totalOutstanding: number
  nextDueDate: string | null
  nextDueAmount: number | null
}
export interface LoanDetailDto {
  summary: LoanSummaryDto
  originationFee: number
  decisionReason: string
  disbursementAccountId: string
  disbursementAccountNumber: string
  schedule: LoanInstallmentDto[]
}
export interface ApplyLoanResponse {
  decision: string
  creditScore: number
  reason: string
  loan: LoanDetailDto
}

export interface StatementLine {
  entryId: number
  transactionId: string
  reference: string
  type: string
  amount: number
  balanceAfter: number
  description: string
  postedAt: string
}
export interface Paged<T> {
  items: T[]
  page: number
  pageSize: number
  total: number
  totalPages: number
}

export class ApiError extends Error {
  constructor(public status: number, public code: string, message: string) {
    super(message)
  }
}

export function getAccessToken() { return localStorage.getItem(ACCESS_KEY) }
function getRefreshToken() { return localStorage.getItem(REFRESH_KEY) }
function setTokens(pair: TokenPairPayload | null) {
  if (!pair) {
    localStorage.removeItem(ACCESS_KEY)
    localStorage.removeItem(REFRESH_KEY)
    return
  }
  localStorage.setItem(ACCESS_KEY, pair.accessToken)
  localStorage.setItem(REFRESH_KEY, pair.refreshToken)
}

/** Attempt a token refresh exactly once — single-flight so parallel 401s don't race. */
let refreshInFlight: Promise<boolean> | null = null
async function tryRefresh(): Promise<boolean> {
  if (refreshInFlight) return refreshInFlight
  refreshInFlight = (async () => {
    const refreshToken = getRefreshToken()
    if (!refreshToken) return false
    try {
      const res = await fetch(`${baseUrl}/api/auth/refresh`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken }),
      })
      if (!res.ok) return false
      setTokens((await res.json()) as TokenPairPayload)
      return true
    } catch {
      return false
    } finally {
      refreshInFlight = null
    }
  })()
  return refreshInFlight
}

function onUnauthorized() {
  setTokens(null)
  window.dispatchEvent(new Event('meridian:unauthorized'))
}

async function parseError(res: Response): Promise<ApiError> {
  let code = `http_${res.status}`
  let message = `Request failed (${res.status})`
  try {
    const body = await res.json()
    code = body.code ?? body.error ?? body.title ?? code
    message = body.detail ?? body.error ?? body.title ?? message
  } catch { /* non-JSON error */ }
  return new ApiError(res.status, code, message)
}

async function request<T>(path: string, init: RequestInit = {}, retry = true): Promise<T> {
  const headers = new Headers(init.headers)
  headers.set('Content-Type', 'application/json')
  const token = getAccessToken()
  if (token) headers.set('Authorization', `Bearer ${token}`)

  const res = await fetch(`${baseUrl}${path}`, { ...init, headers })

  if (res.status === 401 && !path.startsWith('/api/auth/') && retry) {
    if (await tryRefresh()) return request<T>(path, init, /* retry */ false)
    onUnauthorized()
    throw await parseError(res)
  }
  if (!res.ok) throw await parseError(res)
  if (res.status === 204) return undefined as T
  return (await res.json()) as T
}

export const api = {
  register: (email: string, password: string, fullName: string) =>
    request<TokenPairPayload>('/api/auth/register', {
      method: 'POST',
      body: JSON.stringify({ email, password, fullName }),
    }).then((r) => (setTokens(r), r)),

  login: (email: string, password: string, totpCode?: string) =>
    request<TokenPairPayload>('/api/auth/login', {
      method: 'POST',
      body: JSON.stringify({ email, password, totpCode: totpCode || undefined }),
    }).then((r) => (setTokens(r), r)),

  logout: async () => {
    const refreshToken = getRefreshToken()
    try {
      if (refreshToken) {
        await fetch(`${baseUrl}/api/auth/logout`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ refreshToken }),
        })
      }
    } finally {
      setTokens(null)
    }
  },
  logoutAll: () => request<void>('/api/auth/logout-all', { method: 'POST' }),

  me: () => request<MePayload>('/api/auth/me'),

  changePassword: (currentPassword: string, newPassword: string) =>
    request<void>('/api/auth/change-password', {
      method: 'POST',
      body: JSON.stringify({ currentPassword, newPassword }),
    }),

  setup2fa: () =>
    request<{ secretBase32: string; otpAuthUri: string }>('/api/auth/2fa/setup', { method: 'POST' }),
  enable2fa: (code: string) =>
    request<void>('/api/auth/2fa/enable', { method: 'POST', body: JSON.stringify({ code }) }),
  disable2fa: (code: string) =>
    request<void>('/api/auth/2fa/disable', { method: 'POST', body: JSON.stringify({ code }) }),

  listAccounts: () => request<Account[]>('/api/accounts'),
  openAccount: (type: 'Savings' | 'Current', currency: string) =>
    request<Account>('/api/accounts', { method: 'POST', body: JSON.stringify({ type, currency }) }),

  deposit: (accountId: string, amount: number, description: string, idempotencyKey: string) =>
    request<MoneyResult>('/api/transactions/deposit', {
      method: 'POST',
      headers: { 'Idempotency-Key': idempotencyKey },
      body: JSON.stringify({ accountId, amount, description }),
    }),
  withdraw: (accountId: string, amount: number, description: string, idempotencyKey: string) =>
    request<MoneyResult>('/api/transactions/withdraw', {
      method: 'POST',
      headers: { 'Idempotency-Key': idempotencyKey },
      body: JSON.stringify({ accountId, amount, description }),
    }),
  transfer: (fromAccountId: string, toAccountNumber: string, amount: number, description: string, idempotencyKey: string) =>
    request<MoneyResult>('/api/transactions/transfer', {
      method: 'POST',
      headers: { 'Idempotency-Key': idempotencyKey },
      body: JSON.stringify({ fromAccountId, toAccountNumber, amount, description }),
    }),

  statement: (accountId: string, opts: { from?: string; to?: string; page?: number; pageSize?: number } = {}) => {
    const q = new URLSearchParams()
    if (opts.from) q.set('from', opts.from)
    if (opts.to) q.set('to', opts.to)
    q.set('page', String(opts.page ?? 1))
    q.set('pageSize', String(opts.pageSize ?? 15))
    return request<Paged<StatementLine>>(`/api/accounts/${accountId}/statement?${q}`)
  },

  /** Authenticated CSV download. */
  statementCsv: async (accountId: string, from?: string, to?: string) => {
    const q = new URLSearchParams()
    if (from) q.set('from', from)
    if (to) q.set('to', to)
    q.set('format', 'csv')
    const doFetch = async () => {
      const headers = new Headers()
      const token = getAccessToken()
      if (token) headers.set('Authorization', `Bearer ${token}`)
      return fetch(`${baseUrl}/api/accounts/${accountId}/statement?${q}`, { headers })
    }
    let res = await doFetch()
    if (res.status === 401 && (await tryRefresh())) res = await doFetch()
    if (!res.ok) throw await parseError(res)
    const blob = await res.blob()
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `statement-${accountId.slice(0, 8)}.csv`
    a.click()
    URL.revokeObjectURL(url)
  },

  // ---- admin ----
  adminFreeze: (id: string, reason?: string) =>
    request<void>(`/api/admin/accounts/${id}/freeze`, { method: 'POST', body: JSON.stringify({ reason }) }),
  adminUnfreeze: (id: string, reason?: string) =>
    request<void>(`/api/admin/accounts/${id}/unfreeze`, { method: 'POST', body: JSON.stringify({ reason }) }),
  adminClose: (id: string, reason?: string) =>
    request<void>(`/api/admin/accounts/${id}/close`, { method: 'POST', body: JSON.stringify({ reason }) }),
  adminReverse: (transactionId: string, description: string, idempotencyKey: string) =>
    request<MoneyResult>(`/api/admin/transactions/${transactionId}/reverse`, {
      method: 'POST',
      headers: { 'Idempotency-Key': idempotencyKey },
      body: JSON.stringify({ description }),
    }),
  // ---- overdraft ----
  overdraftTerms: () => request<OverdraftProductTerms[]>('/api/overdraft/terms'),
  myOverdrafts: () => request<OverdraftFacilityDto[]>('/api/overdraft'),
  applyOverdraft: (input: { accountId: string; productCode: 'ODL-VAR' | 'ODL-SEC'; requestedLimit: number; collateralAccountId?: string }) =>
    request<OverdraftFacilityDto>('/api/overdraft/apply', {
      method: 'POST',
      body: JSON.stringify(input),
    }),
  closeOverdraft: (id: string, reason?: string) =>
    request<OverdraftFacilityDto>(`/api/overdraft/${id}/close`, {
      method: 'POST',
      body: JSON.stringify({ reason: reason ?? null }),
    }),

  // ---- loans ----
  loanProducts: () => request<LoanProductDto[]>('/api/loans/products'),
  applyLoan: (input: { loanProductCode: string; principal: number; termMonths: number; purpose: string; disbursementAccountId: string }) =>
    request<ApplyLoanResponse>('/api/loans/apply', {
      method: 'POST',
      body: JSON.stringify(input),
    }),
  listLoans: () => request<LoanSummaryDto[]>('/api/loans'),
  getLoan: (id: string) => request<LoanDetailDto>(`/api/loans/${id}`),
  acceptLoan: (id: string) =>
    request<LoanDetailDto>(`/api/loans/${id}/accept-and-disburse`, { method: 'POST', body: '{}' }),
  cancelLoan: (id: string) =>
    request<void>(`/api/loans/${id}/cancel`, { method: 'POST', body: '{}' }),

  // ---- KYC (customer) ----
  kycStatus: () => request<KycStatusPayload>('/api/kyc/status'),
  kycProfile: () => request<KycProfilePayload>('/api/kyc/profile'),
  kycUpdateProfile: (profile: KycProfileInput) =>
    request<KycProfilePayload>('/api/kyc/profile', {
      method: 'PUT',
      body: JSON.stringify(profile),
    }),
  kycDocuments: () => request<KycDocumentMeta[]>('/api/kyc/documents'),
  kycUploadDocument: async (docType: string, file: File) => {
    const form = new FormData()
    form.append('docType', docType)
    form.append('file', file)
    const headers = new Headers()
    const token = getAccessToken()
    if (token) headers.set('Authorization', `Bearer ${token}`)
    const res = await fetch(`${baseUrl}/api/kyc/documents`, { method: 'POST', headers, body: form })
    if (res.status === 401 && (await tryRefresh())) {
      const retry = new Headers()
      const t2 = getAccessToken()
      if (t2) retry.set('Authorization', `Bearer ${t2}`)
      const res2 = await fetch(`${baseUrl}/api/kyc/documents`, { method: 'POST', headers: retry, body: form })
      if (!res2.ok) throw await parseError(res2)
      return (await res2.json()) as KycDocumentMeta
    }
    if (!res.ok) throw await parseError(res)
    return (await res.json()) as KycDocumentMeta
  },
  kycDownloadDocument: async (id: string, fileName: string) => {
    const doFetch = async () => {
      const headers = new Headers()
      const token = getAccessToken()
      if (token) headers.set('Authorization', `Bearer ${token}`)
      return fetch(`${baseUrl}/api/kyc/documents/${id}/content`, { headers })
    }
    let res = await doFetch()
    if (res.status === 401 && (await tryRefresh())) res = await doFetch()
    if (!res.ok) throw await parseError(res)
    const blob = await res.blob()
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = fileName
    a.click()
    URL.revokeObjectURL(url)
  },
  kycDeleteDocument: (id: string) =>
    request<void>(`/api/kyc/documents/${id}`, { method: 'DELETE' }),

  // ---- admin: KYC review ----
  adminPendingKyc: (page = 1, pageSize = 50) =>
    request<{ items: PendingProfileDto[]; page: number; pageSize: number; total: number }>(
      `/api/admin/kyc/pending?page=${page}&pageSize=${pageSize}`),
  adminApproveKyc: (userId: string) =>
    request<void>(`/api/admin/kyc/${userId}/approve`, { method: 'POST', body: '{}' }),
  adminRejectKyc: (userId: string, reason: string) =>
    request<void>(`/api/admin/kyc/${userId}/reject`, {
      method: 'POST',
      body: JSON.stringify({ reason }),
    }),
  adminCustomerDocuments: (userId: string) =>
    request<KycDocumentMeta[]>(`/api/admin/kyc/${userId}/documents`),

  // ---- admin: AML alerts ----
  adminAmlAlerts: (status: 'Open' | 'Reviewed' | 'Dismissed' | '' = 'Open', page = 1, pageSize = 50) => {
    const q = new URLSearchParams({ page: String(page), pageSize: String(pageSize) })
    if (status) q.set('status', status)
    return request<{ items: AmlAlertDto[]; total: number }>(`/api/admin/aml/alerts?${q}`)
  },
  adminReviewAlert: (id: string, note?: string) =>
    request<void>(`/api/admin/aml/alerts/${id}/review`, {
      method: 'POST',
      body: JSON.stringify({ note: note ?? null }),
    }),
  adminDismissAlert: (id: string, note?: string) =>
    request<void>(`/api/admin/aml/alerts/${id}/dismiss`, {
      method: 'POST',
      body: JSON.stringify({ note: note ?? null }),
    }),

  // ---- admin: overdraft portfolio ----
  adminOverdrafts: (status: 'Pending' | 'Active' | 'Closed' | 'Rejected' | '' = 'Active', page = 1, pageSize = 50) => {
    const q = new URLSearchParams({ page: String(page), pageSize: String(pageSize) })
    if (status) q.set('status', status)
    return request<{ items: AdminOverdraftRow[]; total: number }>(`/api/admin/overdraft?${q}`)
  },

  // ---- admin: loan portfolio ----
  adminLoans: (status: 'Approved' | 'Active' | 'Completed' | 'Rejected' | 'Cancelled' | '' = 'Active', page = 1, pageSize = 50) => {
    const q = new URLSearchParams({ page: String(page), pageSize: String(pageSize) })
    if (status) q.set('status', status)
    return request<{ items: AdminLoanRow[]; total: number }>(`/api/admin/loans?${q}`)
  },

  // ---- admin: batch & GL ----
  adminRunDayEnd: (date?: string) =>
    request<BatchRunDto>('/api/admin/batch/day-end', {
      method: 'POST',
      body: JSON.stringify({ date: date ?? null }),
    }),
  adminCapitalizeInterest: (asOf: string) =>
    request<BatchRunDto>('/api/admin/batch/capitalize-interest', {
      method: 'POST',
      body: JSON.stringify({ asOf }),
    }),
  adminChargeFees: (year: number, month: number) =>
    request<BatchRunDto>('/api/admin/batch/charge-fees', {
      method: 'POST',
      body: JSON.stringify({ year, month }),
    }),
  adminBatchRuns: (count = 20) =>
    request<BatchRunDto[]>(`/api/admin/batch/runs?count=${count}`),
  adminTrialBalance: () => request<TrialBalance>('/api/admin/gl/trial-balance'),

  adminAudit: (page = 1, pageSize = 50, action?: string) => {
    const q = new URLSearchParams({ page: String(page), pageSize: String(pageSize) })
    if (action) q.set('action', action)
    return request<{ items: Array<Record<string, unknown>>; total: number }>(`/api/admin/audit?${q}`)
  },
}
