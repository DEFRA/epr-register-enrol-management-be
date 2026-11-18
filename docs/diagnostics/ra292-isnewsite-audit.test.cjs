'use strict'

// Behaviour tests for the mongosh diagnostic ra292-isnewsite-audit.js, run with
// Node's built-in test runner (no dependencies). The script is a plain mongosh
// script that reads the globals `db`, `print`, `printjson` and
// `RA292_DEPLOYED_AT`, so each test installs stand-ins for those and loads a
// fresh copy of it.
//
//   node --test docs/diagnostics/

const { test, afterEach } = require('node:test')
const assert = require('node:assert/strict')
const path = require('node:path')

const SCRIPT = path.join(__dirname, 'ra292-isnewsite-audit.js')
const GLOBALS = ['db', 'print', 'printjson', 'RA292_DEPLOYED_AT']

function harness(items = []) {
  const state = { output: [], queries: [], collection: null }
  state.collection = {
    find(filter) {
      state.queries.push(filter)
      return { sort: () => ({ forEach: (fn) => items.forEach(fn) }) }
    }
  }
  globalThis.db = { getSiblingDB: () => ({ getCollection: () => state.collection }) }
  globalThis.print = (line) => state.output.push(String(line))
  globalThis.printjson = (value) => state.output.push(JSON.stringify(value))
  return state
}

function load(deployedAt) {
  if (deployedAt === undefined) {
    delete globalThis.RA292_DEPLOYED_AT
  } else {
    globalThis.RA292_DEPLOYED_AT = deployedAt
  }
  delete require.cache[SCRIPT]
  require(SCRIPT)
}

afterEach(() => {
  for (const name of GLOBALS) delete globalThis[name]
  delete require.cache[SCRIPT]
})

test('refuses to run when RA292_DEPLOYED_AT is not set', () => {
  const state = harness()
  assert.throws(() => load(undefined), {
    name: 'Error',
    message: /RA292_DEPLOYED_AT is not set/
  })
  assert.equal(state.queries.length, 0)
})

test('refuses to run when RA292_DEPLOYED_AT is empty', () => {
  const state = harness()
  assert.throws(() => load(''), { message: /RA292_DEPLOYED_AT is not set/ })
  assert.equal(state.queries.length, 0)
})

test('refuses to run when RA292_DEPLOYED_AT is not a date-time', () => {
  const state = harness()
  assert.throws(() => load('not-a-date'), {
    name: 'TypeError',
    message: /RA292_DEPLOYED_AT is not-a-date, which is not a date-time/
  })
  assert.equal(state.queries.length, 0)
})

test('bounds the query at the supplied deploy time and classifies the sites', () => {
  const state = harness([
    {
      _id: 'item-1',
      payload: {
        overseasSites: {
          sites: [
            { siteName: 'ReEx site', isNewSite: true },
            { siteName: 'Operator site', isNewSite: true, orsId: '001' }
          ]
        }
      }
    }
  ])

  load('2026-08-11T09:00:00Z')

  assert.equal(state.queries.length, 1)
  const window = state.queries[0].submittedAt
  assert.equal(window.$gte.toISOString(), '2026-07-26T00:00:00.000Z')
  assert.equal(window.$lt.toISOString(), '2026-08-11T09:00:00.000Z')
  assert.ok(
    state.output.some((line) =>
      line.includes('Window end   : 2026-08-11T09:00:00.000Z  (supplied RA-292 deploy time)')
    )
  )
  assert.ok(state.output.some((line) => /PROVABLY-CORRUPT .*: 1$/.test(line)))
  assert.ok(state.output.some((line) => /ALREADY-CORRECT .*: 1$/.test(line)))
})

test('replaces every mutating collection method with a throw', () => {
  const state = harness()
  load('2026-08-11T09:00:00Z')
  assert.throws(() => state.collection.insertOne({}), /READ-ONLY diagnostic/)
  assert.throws(() => state.collection.deleteMany({}), /READ-ONLY diagnostic/)
})
