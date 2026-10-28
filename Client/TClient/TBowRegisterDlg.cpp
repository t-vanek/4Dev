#include "stdafx.h"
#include "TBowRegisterDlg.h"
#include "TClientGame.h"
#include "TClientWnd.h"

CTBowRegisterDlg::CTBowRegisterDlg( TComponent *pParent, FRAMEDESC_SHAREDPTR pDesc )
: CTClientUIBase( pParent, pDesc )
{
	m_pButton = (TButton*) FindKid(28112);
	m_pButton->m_menu[TNM_LCLICK] = GM_BOW_REGISTER;
	m_pButton->m_strText = CTChart::Localize("Register");
	m_pText = FindKid(28113);
	m_pText->m_strText = CTChart::Localize("Derion have formed an alliance to fight the Valorians and they need your help! Will you join them?");

	FindKid(ID_CTRLINST_TITLE)->m_strText = CTChart::Localize("Register to Battle of the Worlds");
}

CTBowRegisterDlg::~CTBowRegisterDlg()
{

}

void CTBowRegisterDlg::SetType(BYTE bType)
{
	CTClientGame* pGAME = CTClientGame::GetInstance();
	CTClientChar* pCHAR = pGAME->GetMainChar();
	if (!pCHAR)
		return;

	m_bType = bType;

	switch (bType)
	{
	case SYSTEM_BOW:
		{
			switch (pCHAR->GetWarCountry())
			{
			case TCONTRY_D:
				m_pText->m_strText = CTChart::Localize("Valorian have formed an alliance to fight the Derions and they need your help! Will you join them?");
				break;
			case TCONTRY_C:
				m_pText->m_strText = CTChart::Localize("Derion have formed an alliance to fight the Valorians and they need your help! Will you join them?");
				break;
			}
		}
		break;
	case SYSTEM_BR:
		m_pText->m_strText = CTChart::Localize("Would you like to register to the Battle Royal? You will get a team of 2 more people, and you will fight against everyone.");
		break;
	}
}

void CTBowRegisterDlg::ShowComponent(BOOL bVisible)
{
	if (!bVisible)
	{
		CTClientUIBase::ShowComponent(bVisible);
		return;
	}

	CTClientGame* pGAME = CTClientGame::GetInstance();
	CTClientChar* pCHAR = pGAME->GetMainChar();

	if (!pCHAR)
		return;

	if (pCHAR->GetWarCountry() > TCONTRY_C)
	{
		pGAME->GetMainWnd()->MessageBoxOK(
			CTChart::Localize("You cannot participate the battle unless you choose a country."),
			TSTR_OK,
			GM_CLOSE_MSGBOX,
			0 );

		return;
	}

	CTClientUIBase::ShowComponent(bVisible);
}

BOOL CTBowRegisterDlg::CanWithItemUI()
{
	return TRUE;
}