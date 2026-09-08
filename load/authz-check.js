// Load profile for the decision endpoint.
//
//   k6 run load/authz-check.js
//   k6 run -e HOST=http://localhost:5120 -e RATE=500 -e DURATION=5m load/authz-check.js
//
// The arrival rate is fixed rather than derived from a fixed number of virtual users, because the claim
// being tested is about latency at a given request rate. A closed-loop test with a slow server simply
// makes fewer requests and reports a flattering percentile.
//
// k6 is AGPL-3.0 and runs as an external tool. Nothing in this repository links against it, so it does
// not appear in the dependency tree the licence audit walks.

import http from 'k6/http';
import { check } from 'k6';
import { Trend } from 'k6/metrics';

const host = __ENV.HOST || 'http://localhost:5120';
const rate = Number(__ENV.RATE || 500);
const duration = __ENV.DURATION || '60s';

const decision = new Trend('authz_check_ms', true);

export const options = {
  scenarios: {
    check: {
      executor: 'constant-arrival-rate',
      rate,
      timeUnit: '1s',
      duration,
      preAllocatedVUs: 50,
      maxVUs: 300,
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.001'],
    authz_check_ms: ['p(50)<5', 'p(99)<20'],
  },
};

const body = JSON.stringify({
  subject: __ENV.SUBJECT || 'user:alice',
  relation: __ENV.RELATION || 'viewer',
  resource: __ENV.RESOURCE || 'document:onboarding',
});

export default function () {
  const response = http.post(`${host}/authz/check`, body, {
    headers: { 'Content-Type': 'application/json' },
  });

  decision.add(response.timings.duration);

  check(response, {
    'decided': (r) => r.status === 200 && r.json('requestId') !== undefined,
  });
}
