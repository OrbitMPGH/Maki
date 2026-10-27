---
title: Two-factor and API keys
description: Turn on two-factor sign-in, keep your recovery codes safe, and create per-user API keys for scripts and apps.
---

Two-factor sign-in protects your password. API keys let scripts and apps call Maki as you. Both belong to one account, so one reader's setup never changes anyone else's.

## Turn on two-factor

Open **Settings → Account → Sign-in** and click **Set up** in the Two-factor authentication card.

1. Scan the QR code with an authenticator app, or type the key shown under it into the app.
2. Enter the code from the app and your password, then click **Verify and enable**.
3. Save the recovery codes Maki shows you.

The key only becomes active once a code verifies, so a mistyped key cannot lock you out. Maki shows the recovery codes once and stores them hashed, so nobody can read them back, including you. Keep them somewhere you can reach without your phone.

Each code works once. If Maki says a code was already used, wait for the next one.

When you sign in, Maki asks for the code after your password. Tick **Trust this device for 30 days** to skip that prompt in the same browser. If you cannot reach your app, choose **Use a recovery code** instead.

The Sign-in card shows how many recovery codes you have left. **New recovery codes** asks for your password and a current code, and replaces the old set. **Disable** asks for your password. Turning two-factor off signs out your other sessions.

Admins can reset two-factor for another account from **Settings → Users**, for example after someone loses their phone.

Sign-in and two-factor attempts are limited to 10 a minute per client address. Past that, Maki refuses for a moment and tells the browser when to retry.

:::tip
Turn two-factor on before you expose Maki to the internet. [Reverse proxy and HTTPS](/Maki/installation/reverse-proxy-https/) covers the rest of that setup.
:::

### Single sign-on

If you sign in through single sign-on, Maki does not ask for its code on that path. Your identity provider handles the second factor. A code you set up still protects password sign-in. See [Single sign-on](/Maki/installation/single-sign-on/).

## API keys

Open **Settings → Account → API keys**.

Type a name for the key, such as "Phone reader", enter your password, and click **Create**. An account without a password (single sign-on only) must have signed in within the last ten minutes, or Maki refuses to create the key.

Maki shows the new key in a dialog titled **Your new API key**. Copy it then. Maki stores only a fingerprint, so the key cannot be shown again. If you lose it, revoke it and create another.

The table lists each key's name, prefix, and when it was last used. Create one key per script or app, so you can revoke one without touching the rest.

**Revoke** stops anything using that key straight away. It cannot be undone. Revoked keys stay in the table, greyed out.

### What a key can reach

A key has the same access as the account that made it. It works across the REST API under `/api/v1`. Send it in the `X-Api-Key` header:

```
X-Api-Key: <your key>
```

Maki reads keys from that header only. It does not accept them in the query string, because URLs end up in browser history and proxy logs.

OPDS feed tokens are a separate kind of key. They work only on the OPDS catalogue, and Maki rejects them on `/api/v1`. A reading app that holds a feed URL therefore cannot manage your library. See [OPDS apps](/Maki/guides/reading/opds-apps/) for setting one up.

If you restore a backup, any key you revoked after that backup was taken stays revoked. See [Backup and restore](/Maki/guides/running/backup-restore/).
