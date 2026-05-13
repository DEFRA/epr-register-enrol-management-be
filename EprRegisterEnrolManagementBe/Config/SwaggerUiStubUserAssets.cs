namespace EprRegisterEnrolManagementBe.Config;

/// <summary>
/// Static client-side assets that augment the Swagger UI explorer with a
/// stub-user picker. Lets a developer choose one of the local stub users
/// from a dropdown in the topbar instead of typing CDP trust headers into
/// the Authorize modal.
///
/// Wired up by Program.cs only when <see cref="SwaggerUiGating"/> reports
/// that the explorer is enabled, so production OpenAPI tooling never
/// receives this dev-only affordance. RA-124.
/// </summary>
internal static class SwaggerUiStubUserAssets
{
    /// <summary>
    /// Path the JS bundle is served from. Swagger UI loads it via
    /// <c>SwaggerUIOptions.InjectJavascript</c>.
    /// </summary>
    internal const string ScriptPath = "/swagger-ui-stub-users.js";

    /// <summary>
    /// JS function (as a single expression) registered as Swagger UI's
    /// <c>requestInterceptor</c>. Reads the user selected by the topbar
    /// dropdown from <c>localStorage</c> and attaches the four CDP trust
    /// headers that <c>CognitoClientIdAuthenticationHandler</c> consumes
    /// in header-trust (no-shared-secret) mode. Anonymous endpoints
    /// (<c>/health</c>, <c>/openapi/*</c>, the Swagger UI itself) ignore
    /// the headers, so attaching them unconditionally is safe.
    /// </summary>
    internal const string RequestInterceptor = """
        (req) => {
          try {
            const raw = window.localStorage.getItem('eprSwaggerStubUser');
            if (!raw) return req;
            const u = JSON.parse(raw);
            req.headers = req.headers || {};
            req.headers['x-cdp-cognito-client-id'] = 'local-swagger-ui';
            req.headers['x-cdp-user-id'] = u.id;
            req.headers['x-cdp-user-name'] = u.name;
            req.headers['x-cdp-user-roles'] = u.roles;
          } catch (e) {
            console.warn('stub-user interceptor failed', e);
          }
          return req;
        }
        """;

    /// <summary>
    /// JS bundle served at <see cref="ScriptPath"/>. Adds a stub-user
    /// picker to the Swagger UI topbar. Kept as inline source so it ships
    /// with the assembly — no static-file middleware required.
    /// The stub-user list mirrors <c>STUB_USERS</c> in the BFF
    /// (<c>src/server/routes/auth/stub/controller.js</c>); these are dev
    /// fixtures only and not tied to any real identity.
    /// </summary>
    internal const string ScriptBody = """
        (function () {
          'use strict';

          const STORAGE_KEY = 'eprSwaggerStubUser';
          const STUB_USERS = [
            { id: 'stub-standard-1',        name: 'Stub Standard User',  roles: 'standard' },
            { id: 'stub-assign-1',          name: 'Stub Assign User',    roles: 'standard,assign' },
            { id: 'stub-decision-maker-1',  name: 'Stub Decision Maker', roles: 'standard,decision-maker' }
          ];

          function read() {
            try { return JSON.parse(window.localStorage.getItem(STORAGE_KEY)); }
            catch (e) { return null; }
          }

          function write(user) {
            if (user) {
              window.localStorage.setItem(STORAGE_KEY, JSON.stringify(user));
            } else {
              window.localStorage.removeItem(STORAGE_KEY);
            }
          }

          function buildPicker() {
            const wrapper = document.createElement('div');
            wrapper.id = 'epr-stub-user-picker';
            wrapper.style.cssText =
              'display:flex;align-items:center;gap:8px;margin-left:auto;' +
              'padding:0 16px;color:#fff;font-family:sans-serif;font-size:13px;';

            const label = document.createElement('label');
            label.textContent = 'Authenticate as:';
            label.htmlFor = 'epr-stub-user-select';

            const select = document.createElement('select');
            select.id = 'epr-stub-user-select';
            select.style.cssText =
              'padding:4px 6px;border-radius:4px;border:1px solid #ccc;' +
              'background:#fff;color:#333;min-width:220px;';

            const noneOpt = document.createElement('option');
            noneOpt.value = '';
            noneOpt.textContent = '— anonymous —';
            select.appendChild(noneOpt);

            STUB_USERS.forEach(function (u) {
              const opt = document.createElement('option');
              opt.value = u.id;
              opt.textContent = u.name + '  [' + u.roles + ']';
              select.appendChild(opt);
            });

            const current = read();
            if (current) select.value = current.id;

            select.addEventListener('change', function () {
              const picked = STUB_USERS.find(function (u) { return u.id === select.value; });
              write(picked || null);
            });

            wrapper.appendChild(label);
            wrapper.appendChild(select);
            return wrapper;
          }

          function attach() {
            if (document.getElementById('epr-stub-user-picker')) return true;
            const topbar = document.querySelector('.topbar-wrapper');
            if (!topbar) return false;
            topbar.style.display = 'flex';
            topbar.style.alignItems = 'center';
            topbar.appendChild(buildPicker());
            return true;
          }

          // Swagger UI mounts asynchronously; poll for the topbar.
          let attempts = 0;
          const timer = window.setInterval(function () {
            if (attach() || ++attempts > 50) {
              window.clearInterval(timer);
            }
          }, 100);
        })();
        """;
}
