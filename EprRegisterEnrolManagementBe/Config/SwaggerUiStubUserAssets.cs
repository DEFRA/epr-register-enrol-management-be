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
    /// JS bundle served at <see cref="ScriptPath"/>. Two responsibilities:
    /// 1. Add a stub-user picker to the Swagger UI topbar; selection is
    ///    persisted in <c>localStorage</c>.
    /// 2. Monkey-patch <c>window.fetch</c> so every same-origin Try it out
    ///    call carries the four CDP trust headers that
    ///    <c>CognitoClientIdAuthenticationHandler</c> consumes in
    ///    header-trust (no-shared-secret) mode. Patching <c>fetch</c>
    ///    directly rather than going through Swashbuckle's
    ///    <c>UseRequestInterceptor</c> avoids that helper's brittle
    ///    arrow-function parsing — the interceptor was being injected but
    ///    silently dropped, leaving every Try it out as anonymous.
    ///
    /// Kept as inline source so it ships with the assembly — no
    /// static-file middleware required. The stub-user list mirrors
    /// <c>STUB_USERS</c> in the BFF
    /// (<c>src/server/routes/auth/stub/controller.js</c>); these are dev
    /// fixtures only and not tied to any real identity.
    /// </summary>
    internal const string ScriptBody = """
        (function () {
          'use strict';

          const STORAGE_KEY = 'eprSwaggerStubUser';
          const STUB_USERS = [
            { id: 'stub-caseworker-1', name: 'Stub Caseworker One',   roles: 'standard' },
            { id: 'stub-caseworker-2', name: 'Stub Caseworker Two',   roles: 'standard' },
            { id: 'stub-caseworker-3', name: 'Stub Caseworker Three', roles: 'standard' }
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

          // Monkey-patch fetch BEFORE Swagger UI mounts so every Try it
          // out call goes through us. We only attach trust headers for
          // same-origin requests (Swagger UI itself fetches the OpenAPI
          // doc with fetch too — it's also same-origin and the backend
          // serves /openapi/* anonymously, so unconditionally attaching
          // is safe).
          const originalFetch = window.fetch.bind(window);
          window.fetch = function (input, init) {
            const user = read();
            if (!user) return originalFetch(input, init);

            const url = typeof input === 'string' ? input : (input && input.url) || '';
            // Resolve relative URLs against the page so we can compare
            // origins. file:// and other oddities fall through unmodified.
            let sameOrigin = true;
            try {
              const parsed = new URL(url, window.location.href);
              sameOrigin = parsed.origin === window.location.origin;
            } catch (e) { /* leave sameOrigin true */ }
            if (!sameOrigin) return originalFetch(input, init);

            init = init || {};
            const headers = new Headers(init.headers || (input instanceof Request ? input.headers : undefined));
            headers.set('x-cdp-cognito-client-id', 'local-swagger-ui');
            headers.set('x-cdp-user-id', user.id);
            headers.set('x-cdp-user-name', user.name);
            headers.set('x-cdp-user-roles', user.roles);
            init.headers = headers;
            return originalFetch(input, init);
          };

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
