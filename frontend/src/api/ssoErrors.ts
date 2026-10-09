import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'

/**
 * What the server's `SsoErrorCodes` mean. The failed-sign-in and failed-link redirects carry only
 * one of these codes in the query string, never a sentence, so a crafted link can't put text of its
 * own on the page. An unknown code falls back to a generic message for sign-in or for linking.
 */
const SSO_ERRORS: Record<string, MessageDescriptor> = {
  accountDisabled: msg`That account is disabled`,
  accountNotSetUp: msg`That account has not been set up yet`,
  alreadyLinked: msg`This account already has a single sign-on login. Remove it before linking another.`,
  alreadyLinkedOther: msg`That single sign-on account is already linked to a different user`,
  challengeRejected: msg`The identity provider rejected the sign-in request. Check the OIDC client configuration.`,
  incomplete: msg`The sign-in did not complete. Please try again.`,
  linkFailed: msg`Could not link that single sign-on account`,
  linkNeedsPassword: msg`Confirm your password, then start linking single sign-on again`,
  linkNewAccountFailed: msg`Could not link that login to a new account`,
  noAccountLinked: msg`No Maki account is linked to that login`,
  noSubject: msg`The identity provider returned no subject`,
  usernameExists: msg`An account with that username already exists`,
}

const GENERIC = msg`Sign-in failed`
const GENERIC_LINK = msg`Could not link single sign-on to your account`

export function ssoErrorLabel(code: string, fallback: MessageDescriptor = GENERIC): MessageDescriptor {
  return Object.hasOwn(SSO_ERRORS, code) ? SSO_ERRORS[code] : fallback
}

export function ssoLinkErrorLabel(code: string): MessageDescriptor {
  return ssoErrorLabel(code, GENERIC_LINK)
}
