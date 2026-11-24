// Teste de carga da API de consolidação (RNF-02/03/05): 50 RPS sustentados.
//   k6 run tests/LoadTests/consolidation-50rps.js
//   (ou via Docker) docker run --rm -i --network host grafana/k6 run - < tests/LoadTests/consolidation-50rps.js
// Variáveis: CF_URL (default http://localhost:8080), DC_URL (default http://localhost:8081), DURATION (default 15m), ACCOUNTS (default 10)
import http from 'k6/http';
import { check } from 'k6';

const CF = __ENV.CF_URL || 'http://localhost:8080';
const DC = __ENV.DC_URL || 'http://localhost:8081';
const ACCOUNTS = parseInt(__ENV.ACCOUNTS || '10');

export const options = {
  scenarios: {
    consolidation_reads: {
      executor: 'constant-arrival-rate',
      rate: 50,
      timeUnit: '1s',
      duration: __ENV.DURATION || '15m',
      preAllocatedVUs: 50,
      maxVUs: 200,
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.05'],          // >= 95% de sucesso
    http_req_duration: ['p(95)<300'],        // p95 <= 300 ms
  },
};

// Dataset multiempresa: contas para os comerciantes A e B, com lançamentos em vários dias.
export function setup() {
  const tenants = ['A', 'B'].map((m) => {
    const res = http.post(`${CF}/dev/token`, JSON.stringify({ merchant: m }), { headers: { 'Content-Type': 'application/json' } });
    return res.json('access_token');
  });

  const accounts = [];
  for (let i = 0; i < ACCOUNTS; i++) {
    const token = tenants[i % tenants.length];
    const headers = { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` };
    const acc = http.post(`${CF}/api/v1/cash-accounts`,
      JSON.stringify({ name: `Carga ${i}`, currency: 'BRL', timeZone: 'America/Sao_Paulo', openingBalance: 1000 }), { headers });
    const id = acc.json('id');
    for (let d = 0; d < 20; d++) {
      const when = new Date(Date.now() - d * 86400000 - 60000).toISOString();
      http.post(`${CF}/api/v1/transactions`,
        JSON.stringify({ cashAccountId: id, type: d % 3 === 0 ? 'DEBIT' : 'CREDIT', amount: 10 + d, occurredAt: when }),
        { headers: Object.assign({ 'Idempotency-Key': `load-${id}-${d}` }, headers) });
    }
    accounts.push({ id, token });
  }
  return { accounts };
}

export default function (data) {
  const a = data.accounts[Math.floor(Math.random() * data.accounts.length)];
  const to = new Date();
  const from = new Date(Date.now() - 29 * 86400000);
  const fmt = (d) => d.toISOString().slice(0, 10);
  const res = http.get(`${DC}/api/v1/daily-balances?cashAccountId=${a.id}&from=${fmt(from)}&to=${fmt(to)}`,
    { headers: { Authorization: `Bearer ${a.token}` }, tags: { name: 'daily-balances-period' } });
  check(res, { 'status 200': (r) => r.status === 200 });
}
