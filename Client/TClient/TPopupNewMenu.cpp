#include "StdAfx.h"
#include "TClientGame.h"
#include "TCustomStrings.h"
#include "TSecuritySystemDlg.h"

CTPopupNewMenuDlg::CTPopupNewMenuDlg( TComponent *pParent, FRAMEDESC_SHAREDPTR pDesc)
:CTClientUIBase( pParent, pDesc)
{
	DWORD dwCatImgList[TMENUNEW_COUNT] = {
		ID_CTRLINST_CATIMG1,
		ID_CTRLINST_CATIMG2,
		ID_CTRLINST_CATIMG3,
		ID_CTRLINST_CATIMG4,
		ID_CTRLINST_CATIMG5,
		ID_CTRLINST_CATIMG6,
		ID_CTRLINST_CATIMG7
	};

	DWORD dwCatBtn[TMENUNEW_COUNT] = {
		ID_CTRLINST_CATBTN1,
		ID_CTRLINST_CATBTN2,
		ID_CTRLINST_CATBTN3,
		ID_CTRLINST_CATBTN4,
		ID_CTRLINST_CATBTN5,
		ID_CTRLINST_CATBTN6,
		ID_CTRLINST_CATBTN7
	};

	DWORD dwCatName[TMENUNEW_COUNT] = {
		ID_CTRLINST_CATNAME1,
		ID_CTRLINST_CATNAME2,
		ID_CTRLINST_CATNAME3,
		ID_CTRLINST_CATNAME4,
		ID_CTRLINST_CATNAME5,
		ID_CTRLINST_CATNAME6,
		ID_CTRLINST_CATNAME7
	};

	for(BYTE i = 0; i < TMENUNEW_COUNT; i++)
	{
		m_InvenActions[i] = -1;
		m_pCatImg[i] = (TImageList*) FindKid(dwCatImgList[i]);
		m_pCatBtn[i] = (TButton*) FindKid(dwCatBtn[i]);
		m_pCatName[i] = FindKid(dwCatName[i]);
	}
	
	m_pFrameName = FindKid(ID_CTRLINST_TITLE);
	m_pScroll = (TScroll *) FindKid(ID_CTRLINST_SCROLL);

	m_nCurMode = NULL;
}

CTPopupNewMenuDlg::~CTPopupNewMenuDlg()
{
	ClearPopup();
}

void CTPopupNewMenuDlg::ClearPopup()
{
	for(BYTE i = 0; i < TMENUNEW_COUNT; i++)
	{
		m_InvenActions[i] = -1;
		m_pCatImg[i]->ShowComponent(FALSE);
		m_pCatImg[i]->EnableComponent(FALSE);
		RemoveKid(m_pCatImg[i]);

		m_pCatBtn[i]->ShowComponent(FALSE);
		m_pCatBtn[i]->EnableComponent(FALSE);

		m_pCatName[i]->ShowComponent(FALSE);
		m_pCatName[i]->m_strText = "";
	}

	m_pFrameName->ShowComponent(FALSE);
	m_pFrameName->m_strText = "";
	
	m_pScroll->ShowComponent(FALSE);
	m_pScroll->EnableComponent(FALSE);
	RemoveKid(m_pScroll);

	m_nCurMode = NULL;

	ShowComponent(FALSE);
}

void CTPopupNewMenuDlg::ClearButton()
{
	for(BYTE i = 0; i < TMENUNEW_COUNT; i++)
		m_pCatBtn[i]->Select(FALSE);

	ShowComponent(FALSE);
}

void CTPopupNewMenuDlg::SetBlinkMail(BYTE bState)
{
	if(GetCurMode() == TMAINBUTTONNEW_COMMUNITY)
		m_pCatBtn[TMENUNEWMAIL_MAIL]->Select(bState);
}

void CTPopupNewMenuDlg::SetBlinkCD(BYTE bState)
{
	m_pCatBtn[TMENUNEWINVEN_CASHDEPOT]->Select(bState);
}

CString CTPopupNewMenuDlg::GetVKString(enum TKEY_SET eKey)
{
	CTKeySetting *pKeySet = CTKeySetting::GetInstance();
	CString strTITLE;
	WORD wVKey = pKeySet->GetCurVKey(eKey);
	WORD wModKey = pKeySet->GetCurModKey(eKey);
	CString strKey = pKeySet->VKeyToStr(wVKey, wModKey);
	CString strSHORTCUT;

	switch(eKey)
	{
	case TKEY_HELP:
		{
			strTITLE = CTChart::LoadString(TSTR_TOPMENU_HELP);
			strSHORTCUT.Format( "(%s) ", (LPCTSTR)strKey );
			strSHORTCUT += strTITLE;

			return strSHORTCUT;
		}
	case TKEY_INVEN:
		{
			strTITLE = CTChart::LoadString(TSTR_TOPMENU_INVEN);
			strSHORTCUT.Format( "(%s) ", (LPCTSTR)strKey );
			strSHORTCUT += strTITLE;

			return strSHORTCUT;
		}
	case TKEY_FAMERANK:
		{
			strTITLE = CTChart::LoadString(TSTR_TOPMENU_HOF);
			strSHORTCUT.Format( "(%s) ", (LPCTSTR)strKey );
			strSHORTCUT += strTITLE;

			return strSHORTCUT;
		}
	case TKEY_PET_INFO:
		{
			strTITLE = CTChart::LoadString(TSTR_TOPMENU_MOUNT);
			strSHORTCUT.Format( "(%s) ", (LPCTSTR)strKey );
			strSHORTCUT += strTITLE;

			return strSHORTCUT;
		}
	case TKEY_TOGGLE_GUILD_UI:
		{
			strTITLE = CTChart::LoadString(TSTR_TOPMENU_GUILD);
			strSHORTCUT.Format( "(%s) ", (LPCTSTR)strKey );
			strSHORTCUT += strTITLE;

			return strSHORTCUT;
		}
	case TKEY_QUEST_INFO:
		{
			strTITLE = CTChart::LoadString(TSTR_TOPMENU_QUEST);
			strSHORTCUT.Format( "(%s) ", (LPCTSTR)strKey );
			strSHORTCUT += strTITLE;

			return strSHORTCUT;
		}
	case TKEY_SKILL_INFO:
		{
			strTITLE = CTChart::LoadString(TSTR_TOPMENU_SKILL);
			strSHORTCUT.Format( "(%s) ", (LPCTSTR)strKey );
			strSHORTCUT += strTITLE;

			return strSHORTCUT;
		}
	case TKEY_CHAR_INFO:
		{
			strTITLE = CTChart::LoadString(TSTR_TOPMENU_CHAR);
			strSHORTCUT.Format( "(%s) ", (LPCTSTR)strKey );
			strSHORTCUT += strTITLE;

			return strSHORTCUT;
		}
	case TKEY_HUD:
		{
			strTITLE = CTChart::LoadString(TSTR_TOPMENU_RANKING);
			strSHORTCUT.Format( "(%s) ", (LPCTSTR)strKey );
			strSHORTCUT += strTITLE;

			return strSHORTCUT;
		}
	case TKEY_CASH_CAB:
		{
			strTITLE = CTChart::LoadString(TSTR_TOPMENU_CD);
			strSHORTCUT.Format( "(%s) ", (LPCTSTR)strKey );
			strSHORTCUT += strTITLE;

			return strSHORTCUT;
		}
	case TKEY_TITLE:
		{
			strTITLE = CTChart::LoadString(TSTR_TOPMENU_TITLE);
			strSHORTCUT.Format( "(%s) ", (LPCTSTR)strKey );
			strSHORTCUT += strTITLE;

			return strSHORTCUT;
		}

case TKEY_COMPANION:
		{
			strTITLE = CTChart::Localize("Companion");
			strSHORTCUT.Format( "(%s) ", (LPCTSTR)strKey );
			strSHORTCUT += strTITLE;

			return strSHORTCUT;
		}
	default: return "";
	}
}

int CTPopupNewMenuDlg::GetCurMode()
{
	return m_nCurMode;
}

void CTPopupNewMenuDlg::AddSystem()
{
	ClearPopup();

	for(BYTE i = 0; i < TMENUNEWSETTINGS_COUNT; i++)
	{
		AddKid(m_pCatImg[i]);
		m_pCatImg[i]->ShowComponent(TRUE);
		m_pCatImg[i]->EnableComponent(TRUE);

		m_pCatBtn[i]->ShowComponent(TRUE);
		m_pCatBtn[i]->EnableComponent(TRUE);

		m_pCatName[i]->ShowComponent(TRUE);

		switch(i)
		{
		case TMENUNEWSETTINGS_SYSTEM:
			{
				m_pCatImg[i]->SetCurImage(23);
				m_pCatName[i]->m_strText = CTChart::LoadString(TSTR_TOPMENU_SYSTEM);
			}
			break;

		case TMENUNEWSETTINGS_GAME:
			{
				m_pCatImg[i]->SetCurImage(24);
				m_pCatName[i]->m_strText = CTChart::LoadString(TSTR_TOPMENU_GAMEOPT);
			}
			break;

		case TMENUNEWSETTINGS_KEYBOARD:
			{
				m_pCatImg[i]->SetCurImage(25);
				m_pCatName[i]->m_strText = CTChart::LoadString(TSTR_TOPMENU_KEYBOARD);
			}
			break;

		case TMENUNEWSETTINGS_HELP:
			{
				m_pCatImg[i]->SetCurImage(26);
				m_pCatName[i]->m_strText = GetVKString(TKEY_HELP);
			}
			break;

		case TMENUNEWSETTINGS_UILOCK:
			{
				if(CTClientUIBase::m_bDragLock)
					m_pCatName[i]->m_strText = CTChart::LoadString(TSTR_TOPMENU_NOUILOCK);
				else
					m_pCatName[i]->m_strText = CTChart::LoadString(TSTR_TOPMENU_UILOCK);
					
				m_pCatImg[i]->SetCurImage(27);
			}
			break;

		case TMENUNEWSETTINGS_CHARSEL:
			{
				m_pCatImg[i]->SetCurImage(28);
				if (!CTClientGame::IsInBOWMap() && !CTClientGame::IsInBRMap())
					m_pCatName[i]->m_strText = CTChart::LoadString(TSTR_TOPMENU_CHARSEL);
				else
					m_pCatName[i]->m_strText = CTChart::Localize("Leave Battlefield");
			}
			break;

		case TMENUNEWSETTINGS_CLOSEGAME:
			{
				m_pCatImg[i]->SetCurImage(29);
				m_pCatName[i]->m_strText = CTChart::LoadString(TSTR_TOPMENU_QUITGAME);
			}
			break;

		default: break;
		}
	}

	m_pFrameName->ShowComponent(TRUE);
	m_pFrameName->m_strText = CTChart::LoadString(TSTR_TOPMENU_FRAME_SETTINGS);

	m_nCurMode = TMAINBUTTONNEW_SYSTEM;
}

void CTPopupNewMenuDlg::AddInven()
{
	ClearPopup();
	CTSecuritySystemDlg* security = static_cast<CTSecuritySystemDlg*>(CTClientGame::GetInstance()->GetFrame(TFRAME_SECURITYSYS));
	BYTE row = 0;
	for (int action = 0; action < TMENUNEWINVEN_COUNT; ++action)
	{
		if (action == TMENUNEWINVEN_ENDISABLESC && !security->GetExist())
			continue;
		m_InvenActions[row] = action;
		AddKid(m_pCatImg[row]);
		m_pCatImg[row]->ShowComponent(TRUE);
		m_pCatImg[row]->EnableComponent(TRUE);
		m_pCatBtn[row]->ShowComponent(TRUE);
		m_pCatBtn[row]->EnableComponent(TRUE);
		m_pCatName[row]->ShowComponent(TRUE);
		switch (action)
		{
		case TMENUNEWINVEN_INVEN:
			m_pCatImg[row]->SetCurImage(20);
			m_pCatName[row]->m_strText = GetVKString(TKEY_INVEN);
			break;
		case TMENUNEWINVEN_CASHDEPOT:
			m_pCatImg[row]->SetCurImage(21);
			m_pCatName[row]->m_strText = GetVKString(TKEY_CASH_CAB);
			break;
		case TMENUNEWINVEN_CRCHSC:
			m_pCatImg[row]->SetCurImage(38);
			m_pCatName[row]->m_strText = security->GetExist() ? TSTR_SC_CHANGE : TSTR_SC_CREATE;
			break;
		case TMENUNEWINVEN_ENDISABLESC:
			m_pCatImg[row]->SetCurImage(security->GetEnabled() ? 37 : 36);
			m_pCatName[row]->m_strText = security->GetEnabled() ? TSTR_SC_DISABLE : TSTR_SC_ENABLE;
			break;
		case TMENUNEWINVEN_CUSTOMCLOAK:
			m_pCatImg[row]->SetCurImage(60016);
			m_pCatName[row]->m_strText = CTChart::Localize("Custom Cloak");
			break;
		}
		++row;
	}
	m_pFrameName->ShowComponent(TRUE);
	m_pFrameName->m_strText = CTChart::LoadString(TSTR_TOPMENU_FRAME_INVEN);
	m_nCurMode = TMAINBUTTONNEW_INVEN_SLOT;
}

void CTPopupNewMenuDlg::ExecuteInvenAction(int action)
{
	CTClientGame* game = CTClientGame::GetInstance();
	switch (action)
	{
	case TMENUNEWINVEN_INVEN: game->OnGM_TOGGLE_DEFINVEN(); break;
	case TMENUNEWINVEN_CASHDEPOT: game->OnGM_TOGGLE_CASH_CAB(); break;
	case TMENUNEWINVEN_CUSTOMCLOAK: game->OnGM_TOGGLE_CUSTOMCLOAK(); break;
	case TMENUNEWINVEN_CRCHSC:
	case TMENUNEWINVEN_ENDISABLESC:
	{
		CTSecuritySystemDlg* security = static_cast<CTSecuritySystemDlg*>(game->GetFrame(TFRAME_SECURITYSYS));
		security->PrepareDisplay();
		if (action == TMENUNEWINVEN_CRCHSC)
			security->SwitchMode(security->GetExist() ? TDLG_SC_MCHANGE : TDLG_SC_MCREATE);
		else if (security->GetEnabled())
			security->SwitchMode(TDLG_SC_MDISABLE);
		else
			security->SwitchMode(security->GetExist() ? TDLG_SC_MENABLE : TDLG_SC_MCREATE);
		game->EnableUI(TFRAME_SECURITYSYS);
		break;
	}
	}
}


void CTPopupNewMenuDlg::AddChar()
{
	ClearPopup();

	if (CTClientGame::IsInBOWMap() || CTClientGame::IsInBRMap())
	{
		for (BYTE i = 0; i <= TMENUNEWCHAR_QUESTS; ++i)
		{
			AddKid(m_pCatImg[i]);
			m_pCatImg[i]->ShowComponent(TRUE);
			m_pCatImg[i]->EnableComponent(TRUE);

			m_pCatBtn[i]->ShowComponent(TRUE);
			m_pCatBtn[i]->EnableComponent(TRUE);

			m_pCatName[i]->ShowComponent(TRUE);

			switch(i)
			{
			case TMENUNEWCHAR_CHAR:
				{
					m_pCatImg[i]->SetCurImage(13);
					m_pCatName[i]->m_strText = GetVKString(TKEY_CHAR_INFO);
				}
				break;
			case TMENUNEWCHAR_SKILLS:
				{
					m_pCatImg[i]->SetCurImage(14);
					m_pCatName[i]->m_strText = GetVKString(TKEY_SKILL_INFO);
				}
				break;
			case TMENUNEWCHAR_TITLE:
				{
					m_pCatImg[i]->SetCurImage(16);
					m_pCatName[i]->m_strText = GetVKString(TKEY_PET_INFO);
				}
				break;
			case TMENUNEWCHAR_PET:
				{
					m_pCatImg[i]->SetCurImage(17);
					m_pCatName[i]->m_strText = GetVKString(TKEY_COMPANION);
				}
				break;
			case TMENUNEWCHAR_QUESTS:
				{
					m_pCatImg[i]->SetCurImage(6);
					m_pCatName[i]->m_strText = CTChart::LoadString(TSTR_TOPMENU_ACTIONBAR);
				}
				break;
			}
		}

		m_pFrameName->ShowComponent(TRUE);
		m_pFrameName->m_strText = CTChart::LoadString(TSTR_TOPMENU_FRAME_CHAR);

		m_nCurMode = TMAINBUTTONNEW_CHAR;

		return;
	}

	for(BYTE i = 0; i < TMENUNEWCHAR_COUNT; i++)
	{
		AddKid(m_pCatImg[i]);
		m_pCatImg[i]->ShowComponent(TRUE);
		m_pCatImg[i]->EnableComponent(TRUE);

		m_pCatBtn[i]->ShowComponent(TRUE);
		m_pCatBtn[i]->EnableComponent(TRUE);

		m_pCatName[i]->ShowComponent(TRUE);

		switch(i)
		{
		case TMENUNEWCHAR_CHAR:
			{
				m_pCatImg[i]->SetCurImage(13);
				m_pCatName[i]->m_strText = GetVKString(TKEY_CHAR_INFO);
			}
			break;

		case TMENUNEWCHAR_SKILLS:
			{
				m_pCatImg[i]->SetCurImage(14);
				m_pCatName[i]->m_strText = GetVKString(TKEY_SKILL_INFO);
			}
			break;

		case TMENUNEWCHAR_TITLE:
			{
				m_pCatImg[i]->SetCurImage(15);
				m_pCatName[i]->m_strText = GetVKString(TKEY_TITLE);
			}
			break;

		case TMENUNEWCHAR_PET:
			{
				m_pCatImg[i]->SetCurImage(16);
				m_pCatName[i]->m_strText = GetVKString(TKEY_PET_INFO);
			}
			break;


		case TMENUNEWCHAR_COMPANION:
			{
				m_pCatImg[i]->SetCurImage(17);
				m_pCatName[i]->m_strText = GetVKString(TKEY_COMPANION);
			}
			break;
		case TMENUNEWCHAR_QUESTS:
			{
				m_pCatImg[i]->SetCurImage(18);
				m_pCatName[i]->m_strText = GetVKString(TKEY_QUEST_INFO);
			}
			break;

		case TMENUNEWCHAR_PRIVATESHOP:
			{
				m_pCatImg[i]->SetCurImage(19);
				m_pCatName[i]->m_strText = CTChart::LoadString(TSTR_TOPMENU_PS);
			}
			break;

		default: break;
		}
	}

	m_pFrameName->ShowComponent(TRUE);
	m_pFrameName->m_strText = CTChart::LoadString(TSTR_TOPMENU_FRAME_CHAR);

	m_nCurMode = TMAINBUTTONNEW_CHAR;
}


void CTPopupNewMenuDlg::AddCommunity()
{
	ClearPopup();

	for(BYTE i = 0; i < 1; i++)
	{
		AddKid(m_pCatImg[i]);
		m_pCatImg[i]->ShowComponent(TRUE);
		m_pCatImg[i]->EnableComponent(TRUE);

		m_pCatBtn[i]->ShowComponent(TRUE);
		m_pCatBtn[i]->EnableComponent(TRUE);

		m_pCatName[i]->ShowComponent(TRUE);

		switch(i)
		{
		case TMENUNEWMAIL_MAIL:
			{
				m_pCatImg[i]->SetCurImage(10);
				m_pCatName[i]->m_strText = CTChart::LoadString(TSTR_TOPMENU_MB);
			}
			break;
		/*case TMENUNEWMAIL_TWITTER:
			{
				m_pCatImg[i]->SetCurImage(11);
				m_pCatName[i]->m_strText = "Twitter";
			}
			break;
			*/

		default: break;
		}
	}

	m_pFrameName->ShowComponent(TRUE);
	m_pFrameName->m_strText = CTChart::LoadString(TSTR_TOPMENU_FRAME_MESSENGER);

	m_nCurMode = TMAINBUTTONNEW_COMMUNITY;
}


void CTPopupNewMenuDlg::AddWar()
{
	ClearPopup();

	for(BYTE i = 0; i < TMENUNEWWAR_COUNT; i++)
	{
		AddKid(m_pCatImg[i]);
		m_pCatImg[i]->ShowComponent(TRUE);
		m_pCatImg[i]->EnableComponent(TRUE);

		m_pCatBtn[i]->ShowComponent(TRUE);
		m_pCatBtn[i]->EnableComponent(TRUE);

		m_pCatName[i]->ShowComponent(TRUE);

		switch(i)
		{
		case TMENUNEWWAR_WAR:
			{
				m_pCatImg[i]->SetCurImage(7);
				m_pCatName[i]->m_strText = CTChart::LoadString(TSTR_TOPMENU_WAR);
			}
			break;

		case TMENUNEWWAR_TOH:
			{
				m_pCatImg[i]->SetCurImage(8);
				m_pCatName[i]->m_strText = GetVKString(TKEY_FAMERANK);
			}
			break;

		case TMENUNEWWAR_RANKING:
			{
				m_pCatImg[i]->SetCurImage(9);
				m_pCatName[i]->m_strText = GetVKString(TKEY_HUD);
			}
			break;

		default: break;
		}
	}

	m_pFrameName->ShowComponent(TRUE);
	m_pFrameName->m_strText = CTChart::LoadString(TSTR_TOPMENU_FRAME_WAR);

	m_nCurMode = TMAINBUTTONNEW_WAR;
}


void CTPopupNewMenuDlg::AddHelp()
{
	ClearPopup();

	for(BYTE i = 0; i < TMENUNEWCOMMUNITY_COUNT; i++)
	{
		AddKid(m_pCatImg[i]);
		m_pCatImg[i]->ShowComponent(TRUE);
		m_pCatImg[i]->EnableComponent(TRUE);

		m_pCatBtn[i]->ShowComponent(TRUE);
		m_pCatBtn[i]->EnableComponent(TRUE);

		m_pCatName[i]->ShowComponent(TRUE);

		switch(i)
		{
		case TMENUNEWCOMMUNITY_GUILD:
			{
				m_pCatImg[i]->SetCurImage(2);
				m_pCatName[i]->m_strText = GetVKString(TKEY_TOGGLE_GUILD_UI);
			}
			break;

		case TMENUNEWCOMMUNITY_FRIEND:
			{
				m_pCatImg[i]->SetCurImage(3);
				m_pCatName[i]->m_strText = CTChart::LoadString(TSTR_TOPMENU_FRIEND);
			}
			break;

		case TMENUNEWCOMMUNITY_IGNORE:
			{
				m_pCatImg[i]->SetCurImage(5);
				m_pCatName[i]->m_strText = CTChart::LoadString(TSTR_TOPMENU_IGNORE);
			}
			break;

		case TMENUNEWCOMMUNITY_ACTIONBAR:
			{
				m_pCatImg[i]->SetCurImage(6);
				m_pCatName[i]->m_strText = CTChart::LoadString(TSTR_TOPMENU_ACTIONBAR);
			}
			break;

		default: break;
		}
	}

	m_pFrameName->ShowComponent(TRUE);
	m_pFrameName->m_strText = CTChart::LoadString(TSTR_TOPMENU_FRAME_COMMUNITY);

	m_nCurMode = TMAINBUTTONNEW_HELP;
}

void CTPopupNewMenuDlg::OnLButtonUp( UINT nFlags, CPoint pt)
{
	CTClientGame *pGame = CTClientGame::GetInstance();

	if(m_nCurMode == TMAINBUTTONNEW_HELP)
	{
		if(m_pCatBtn[TMENUNEWCOMMUNITY_GUILD]->HitTest(pt) && m_pCatBtn[TMENUNEWCOMMUNITY_GUILD]->IsVisible())
		{
			ClearButton();
			pGame->ResetPopupMenu();
			pGame->OnGM_TOGGLE_GUILD_BASE_NEW();
		}
		else if(m_pCatBtn[TMENUNEWCOMMUNITY_FRIEND]->HitTest(pt) && m_pCatBtn[TMENUNEWCOMMUNITY_FRIEND]->IsVisible())
		{
			ClearButton();
			pGame->ResetPopupMenu();
			pGame->OnGM_TOGGLE_MESSENGER();
		}
		else if(m_pCatBtn[TMENUNEWCOMMUNITY_IGNORE]->HitTest(pt) && m_pCatBtn[TMENUNEWCOMMUNITY_IGNORE]->IsVisible())
		{
			ClearButton();
			pGame->ResetPopupMenu();
			pGame->OnGM_TOGGLE_BLOCKLIST();
		}
		else if(m_pCatBtn[TMENUNEWCOMMUNITY_ACTIONBAR]->HitTest(pt) && m_pCatBtn[TMENUNEWCOMMUNITY_ACTIONBAR]->IsVisible())
		{
			ClearButton();
			pGame->ResetPopupMenu();
			pGame->OnGM_TOGGLE_ACTLIST();
		}
	}
	else if(m_nCurMode == TMAINBUTTONNEW_WAR)
	{
		if(m_pCatBtn[TMENUNEWWAR_WAR]->HitTest(pt) && m_pCatBtn[TMENUNEWWAR_WAR]->IsVisible())
		{
			ClearButton();
			pGame->ResetPopupMenu();
			pGame->OnGM_WAR_INFO();
		}
		else if(m_pCatBtn[TMENUNEWWAR_TOH]->HitTest(pt) && m_pCatBtn[TMENUNEWWAR_TOH]->IsVisible())
		{
			ClearButton();
			pGame->ResetPopupMenu();
			pGame->OnGM_TOGGLE_FAMERANK();
		}
		else if(m_pCatBtn[TMENUNEWWAR_RANKING]->HitTest(pt) && m_pCatBtn[TMENUNEWWAR_RANKING]->IsVisible())
		{
			ClearButton();
			pGame->ResetPopupMenu();
			pGame->OnGM_RANK_INFO();
		}
	}
	else if(m_nCurMode == TMAINBUTTONNEW_COMMUNITY)
	{
		if(m_pCatBtn[TMENUNEWMAIL_MAIL]->HitTest(pt) && m_pCatBtn[TMENUNEWMAIL_MAIL]->IsVisible())
		{
			ClearButton();
			pGame->ResetPopupMenu();
			pGame->OnGM_TOGGLE_MAIL();
		}
	}
	else if(m_nCurMode == TMAINBUTTONNEW_CHAR)
	{
		if (CTClientGame::IsInBOWMap() || CTClientGame::IsInBRMap())
		{
			if(m_pCatBtn[TMENUNEWCHAR_CHAR]->HitTest(pt) && m_pCatBtn[TMENUNEWCHAR_CHAR]->IsVisible())
			{
				ClearButton();
				pGame->ResetPopupMenu();
				pGame->OnGM_TOGGLE_CHARINFO();
			}
			else if(m_pCatBtn[TMENUNEWCHAR_SKILLS]->HitTest(pt) && m_pCatBtn[TMENUNEWCHAR_SKILLS]->IsVisible())
			{
				ClearButton();
				pGame->ResetPopupMenu();
				pGame->OnGM_TOGGLE_SKILLINFO();
			}
			else if(m_pCatBtn[TMENUNEWCHAR_TITLE]->HitTest(pt) && m_pCatBtn[TMENUNEWCHAR_TITLE]->IsVisible())
			{
				ClearButton();
				pGame->ResetPopupMenu();
				pGame->OnGM_TOGGLE_PETMANAGE();
			}
			else if(m_pCatBtn[TMENUNEWCHAR_PET]->HitTest(pt) && m_pCatBtn[TMENUNEWCHAR_PET]->IsVisible())
			{
				ClearButton();
				pGame->ResetPopupMenu();
				pGame->OnGM_TOGGLE_COMPANION();
			}
			else if(m_pCatBtn[TMENUNEWCHAR_QUESTS]->HitTest(pt) && m_pCatBtn[TMENUNEWCHAR_QUESTS]->IsVisible())
			{
				ClearButton();
				pGame->ResetPopupMenu();
				pGame->OnGM_TOGGLE_ACTLIST();
			}

			return;
		}

		if(m_pCatBtn[TMENUNEWCHAR_CHAR]->HitTest(pt) && m_pCatBtn[TMENUNEWCHAR_CHAR]->IsVisible())
		{
			ClearButton();
			pGame->ResetPopupMenu();
			pGame->OnGM_TOGGLE_CHARINFO();
		}
		else if(m_pCatBtn[TMENUNEWCHAR_SKILLS]->HitTest(pt) && m_pCatBtn[TMENUNEWCHAR_SKILLS]->IsVisible())
		{
			ClearButton();
			pGame->ResetPopupMenu();
			pGame->OnGM_TOGGLE_SKILLINFO();
		}
		else if(m_pCatBtn[TMENUNEWCHAR_TITLE]->HitTest(pt) && m_pCatBtn[TMENUNEWCHAR_TITLE]->IsVisible())
		{
			ClearButton();
			pGame->ResetPopupMenu();
			pGame->OnGM_TOGGLE_TITLE();
		}
		else if(m_pCatBtn[TMENUNEWCHAR_PET]->HitTest(pt) && m_pCatBtn[TMENUNEWCHAR_PET]->IsVisible())
		{
			ClearButton();
			pGame->ResetPopupMenu();
			pGame->OnGM_TOGGLE_PETMANAGE();
		}
	else if(m_pCatBtn[TMENUNEWCHAR_COMPANION]->HitTest(pt) && m_pCatBtn[TMENUNEWCHAR_COMPANION]->IsVisible())
		{
			ClearButton();
			pGame->ResetPopupMenu();
			pGame->OnGM_TOGGLE_COMPANION();
		}
		else if(m_pCatBtn[TMENUNEWCHAR_QUESTS]->HitTest(pt) && m_pCatBtn[TMENUNEWCHAR_QUESTS]->IsVisible())
		{
			ClearButton();
			pGame->ResetPopupMenu();
			pGame->OnGM_TOGGLE_QUESTINFO();
		}
		else if(m_pCatBtn[TMENUNEWCHAR_PRIVATESHOP]->HitTest(pt) && m_pCatBtn[TMENUNEWCHAR_PRIVATESHOP]->IsVisible())
		{
			ClearButton();
			pGame->ResetPopupMenu();
			pGame->OnGM_OPEN_PRIVSHOP_FOR_SELL();
		}
	}
	else if(m_nCurMode == TMAINBUTTONNEW_INVEN_SLOT)
	{
		for (BYTE row = 0; row < TMENUNEW_COUNT; ++row)
			if (m_pCatBtn[row]->IsEnable() && m_pCatBtn[row]->HitTest(pt))
			{
				int action = m_InvenActions[row];
				ClearButton();
				pGame->ResetPopupMenu();
				ExecuteInvenAction(action);
				break;
			}
	}
	else if(m_nCurMode == TMAINBUTTONNEW_SYSTEM)
	{
		if(m_pCatBtn[TMENUNEWSETTINGS_SYSTEM]->HitTest(pt) && m_pCatBtn[TMENUNEWSETTINGS_SYSTEM]->IsVisible())
		{
			ClearButton();
			pGame->ResetPopupMenu();
			pGame->OnGM_SYSTEM_SETTING();
		}
		else if(m_pCatBtn[TMENUNEWSETTINGS_GAME]->HitTest(pt) && m_pCatBtn[TMENUNEWSETTINGS_GAME]->IsVisible())
		{
			ClearButton();
			pGame->ResetPopupMenu();
			pGame->OnGM_GAME_SETTING();
		}
		else if(m_pCatBtn[TMENUNEWSETTINGS_KEYBOARD]->HitTest(pt) && m_pCatBtn[TMENUNEWSETTINGS_KEYBOARD]->IsVisible())
		{
			ClearButton();
			pGame->ResetPopupMenu();
			pGame->OnGM_CONTROL_SETTING();
		}
		else if(m_pCatBtn[TMENUNEWSETTINGS_HELP]->HitTest(pt) && m_pCatBtn[TMENUNEWSETTINGS_HELP]->IsVisible())
		{
			ClearButton();
			pGame->ResetPopupMenu();
			pGame->OnGM_TOGGLE_HELP();
		}
		else if(m_pCatBtn[TMENUNEWSETTINGS_UILOCK]->HitTest(pt) && m_pCatBtn[TMENUNEWSETTINGS_UILOCK]->IsVisible())
		{
			ClearButton();
			pGame->ResetPopupMenu();
			pGame->OnGM_TOGGLE_CHAT_LOCK();
		}
		else if(m_pCatBtn[TMENUNEWSETTINGS_CHARSEL]->HitTest(pt) && m_pCatBtn[TMENUNEWSETTINGS_CHARSEL]->IsVisible())
		{
			ClearButton();
			pGame->ResetPopupMenu();
			pGame->OnGM_SYSTEM();
		}
		else if(m_pCatBtn[TMENUNEWSETTINGS_CLOSEGAME]->HitTest(pt) && m_pCatBtn[TMENUNEWSETTINGS_CLOSEGAME]->IsVisible())
		{
			ClearButton();
			pGame->ResetPopupMenu();
			pGame->OnGM_QUIT();
		}
	}

	CTClientUIBase::OnLButtonUp( nFlags, pt);
}
