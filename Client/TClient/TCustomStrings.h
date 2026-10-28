//Miscellaneous
#define TSTR_GAME_OWNER					"4Story Unreal Reality"
#define TSTR_EMPTY						""
#define TSTR_INCOMPATIBLE_ITEM			"Incompatible item"

#define TSTR_SKILL_ALRINUSE				"You are already using this skill!"
#define TSTR_BUFF_ALRINUSE				"You are already using this Buff !"
#define TSTR_SKILLCOOLDOWN_WARN			"This item still has a cooldown! Please try again when the cooldown has expired."
#define TSTR_ITEMCOOLDOWN_WARN			"This Item still has a Cooldown ! Please try again when the Cooldown has expired."
#define TSTR_FORBIDDEN_WHILEMOVE		"You can' t do this while moving!"
#define TSTR_WHIP_NOTMOUNTED			CTChart::Localize("You only can use your whip on your mount!")
#define TSTR_TWITTERLINK				"http://twitter.com"
#define TSTR_FACEBOOKLINK				"https://www.facebook.com/4Story/"

//Utilitybar
#define TSTR_WALKTO_AUCTIONEER			"Walking to the Auctioneer!"
#define TSTR_WALKTO_MISSINPORTAL		"Walking to the missionportal!"
#define TSTR_WALKTO_SUPPLYMERCHANT		"Walking to the supply merchant!"
#define TSTR_WALKTO_DEPOSIT				"Walking to your private deposit!"
#define TSTR_WALKTO_REPAIR				"Walking to the repair workman!"
#define TSTR_WALKTO_GAMBLE				"Walking to the Mhystican!"
#define TSTR_WALKTO_REFINE				"Walking to the workman for refinements!"
#define TSTR_WALKTO_SMITH				"Walking to the Smith!"
#define TSTR_NOCOUNTRYERR				"You need to select your country before you can use that."

//TITLE SYSTEM
#define TSTR_TITLE_INVALID				"You can't select this title."
#define TSTR_TITLE_AQUIRE				"You aquired the title \"%s\""
#define TSTR_TITLE_CHANGE				"\"%s\" changed to \"%s\""
#define TSTR_TITLE_LOOSE				"You lost the title \"%s\""

//COMPANION SYSTEM
#define TSTR_COMPANION_EXP				"%d / %d"
#define TSTR_COMPANION_HP				"HP: %d"
#define TSTR_COMPANION_LEVEL			CTChart::Localize("Level: %d")
#define TSTR_COMPANION_DESPAWN			CTChart::Localize("Withdraw")
#define TSTR_COMPANION_SPAWN			CTChart::Localize("Call")
#define TSTR_COMPANION_EXHAUSTED		CTChart::Localize("Exhausted")
#define TSTR_COMPANION_NORMAL			CTChart::Localize("Normal")
#define TSTR_COMPANION_HEALTHY			CTChart::Localize("Healthy")
#define TSTR_COMPANION_DELETE			CTChart::Localize("Delete")
#define TSTR_COMPANION_TABONE			CTChart::Localize("Attribute")
#define TSTR_COMPANION_TABTWO			"Bonus"
#define TSTR_COMPANION_END				CTChart::Localize("Endurance")
#define TSTR_COMPANION_INT				CTChart::Localize("Intelligence")
#define TSTR_COMPANION_DEX				CTChart::Localize("Skill")
#define TSTR_COMPANION_WIS				CTChart::Localize("Wisdom")
#define TSTR_COMPANION_STR				CTChart::Localize("Strength")
#define TSTR_COMPANION_MEN				CTChart::Localize("Spirit")
#define TSTR_COMPANION_ATTRPOINT		CTChart::Localize("Status Points")
#define TSTR_COMPANION_LEVELUP			CTChart::Localize("Levelup")
#define TSTR_COMPANION_MAXEXP			CTChart::Localize("Maximum Experience")
																	//bMagic
#define TSTR_COMPANION_ATKRATE			CTChart::Localize("Physical Attack Rate")		//11
#define TSTR_COMPANION_PHYDEF			CTChart::Localize("Evade")						//12
#define TSTR_COMPANION_PHYCRIT			CTChart::Localize("Physical Critical Rate")	//13
#define TSTR_COMPANION_CON				CTChart::Localize("Concetration")				//20
#define TSTR_COMPANION_MCRIT			CTChart::Localize("Magical Critical Rate")		//21
#define TSTR_COMPANION_HPB				CTChart::Localize("Life")						//50
#define TSTR_COMPANION_MPB				"Mana"						//51
#define TSTR_COMPANION_MATKRATE			CTChart::Localize("Magic Attack Rate")			//86
#define TSTR_COMPANION_MDEF				CTChart::Localize("Resistance")				//87
#define TSTR_COMPANION_HONOR			CTChart::Localize("Additional Honor")			//255

#define TSTR_WINDOW_NOTOPEN				"The companion window need to be opened and the pet you want to feed selected."
#define TSTR_COMPANION_MAXLVL			"The selected companion is already on the highest level!"
#define TSTR_COMPANION_MAXHP			"The selected companion cannot have more life than 129600!"

#define TSTR_CREATE_SUCCESSFULL			"The companion was created successfully!"
#define TSTR_CREATE_FAIL				"The companion is not created."
#define TSTR_CREATE_INVALIDNAME			"The name you choosed four your companion is not allowed/too long or contains unallowed symbols."
#define TSTR_CREATE_INVALIDCOMPANION	"The companion which is sealed into this rune is invalid."
#define TSTR_CREATE_ERROR				"A error occured while creating the companion!"
#define TSTR_CREATE_NOFREESLOT			"You have allready the maximum count of companions, before you can create one more you have to delete one!"
#define TSTR_CREATE_ALLREADYSPAWNED		"You have allready spawned a companion! Call him away before creating a new one."

//VETERAN SYSTEM
#define TSTR_VETERAN_DESCRIPTION		"You can choose your startlevel! Depending on the level of the highest character on your account you have up to 3 options to choose between."

//ROJ SYSTEM
#define TSTR_TELEPORT_SUCCESS			"You get automatically teleported into the Ruins of Jotunheim in 1 minute and 30 seconds! "
#define TSTR_COUNTRY_GOR				"You cant use this item before you reached lvl 9 and choosed a Country."
#define TSTR_LEVEL_UNDER75				"You need to be at least level 75 before you can join the Battle in the Ruins."
#define TSTR_WARTIME					"You cannot join the Battle in the Ruins in Wartimes!"
#define TSTR_OWNAGUILD					"You cannot join the Battle in the Ruins if you`re a member of an Guild!"
#define TSTR_ISONSPECIALMAP				"You cant use this Item in specialmaps! "
#define TSTR_PLAYER_PVPMODE				"You cant use this Item while you`re in PvP Mode! "
#define TSTR_ERROR_OCCURED				"A Error occured, please contact the Support! "
#define TSTR_NEEDPERMISSION				"You need to buy a permission pergament before you can use that!"
#define TSTR_USABLE_ONLYINROJ			"You cant use this outside of the Ruins of Jotunheim. "

//REWARDBOX SYSTEM
#define TSTR_REWARDBOX_SUCCESS			"Congratulations! You`ll receive the Items per post. Have Patience."

//TRANSMOG SYSTEM
#define TSTR_TRANSMOG_SUCCESS			"Your item is successful transmogrified."
#define TSTR_CANNOTTRANSMOG_WITH		"This item cannot be transmogrified with the second item."
#define TSTR_TRANSCLEARED_SUCCESS		"Your item transmogrifie is successful cleared."
#define TSTR_TRANS_LASTQ				"You will transmogrifie your item with your selected item. Do you want to continue?"
#define TSTR_TRANSRES_WARN				"You will reset the item transmogrifie. Do you want to continue?"
#define TSTR_TRANSTO					"Transmogrified to :"
#define TSTR_NOSKINABLE					"This item cannot be transmogrified."

//GEM SYSTEM
//#define TSTR_GEM_SUCCESS				"The Gemstone has been successfully attached ! The Gemstone Level has been increased by 1."
//#define TSTR_GEM_FAIL					"The attaching of the Gemstone has failed. All Gemstones have been destroyed !"
#define TSTR_BLUEGEM_FAIL				"The attaching of the Gemstone has failed. Gemstones have been destroyed except two!"
#define TSTR_MAXGEMED					"You have already reached the highest Gemstone Level !"
#define TSTR_BLUEGEM_WARN				"If the attaching of a Gemstone fails and you have more than 2 gemstones on the item you will loose all gemstones except 2."
#define TSTR_GEM_WARN					"If the attaching of a Gemstone fails, all previous Gemstones from the Item will be destroyed! If it succeeds, the Gemstone Level will be increased by 1. Do you want try to attach the Gemstone ?"
#define TSTR_TRANSRES_INVALIDITEM		"The item in the upgrade cannot be transmoged and so there is no possibility to remove a skin."

//AERIAL WAR
#define TSTR_SKYWAR_SETTONORMAL			" The war in the Sky Garden has been set to the normal mode."
#define TSTR_SKYWAR_WARBEGIN			" The war in the Sky Garden has begun."
#define TSTR_SKYWAR_WAREND				" The war in the Sky Garden is finished. Now peace has been restored. It cannot be fought there anymore."
#define	TSTR_SKYWAR_ENDMIN				" The war in the Sky Garden will end in %d minutes."
#define TSTR_SKYWAR_ENDSEC				" The war in the Sky Garden will end in %d seconds."
#define TSTR_SKYWAR_STARTMIN			" The war in the Sky Garden will start in %d minutes."
#define TSTR_SKYWAR_STARTSEC			" The war in the Sky Garden will start in %d seconds."
#define	TSTR_SKYWAR_WON					" won in the Sky Garden."
#define TSTR_SKYWAR_CRAXION				" Derion"
#define TSTR_SKYWAR_DEFUGEL				" Valorian"

//MUSHROOMSPAWN
#define TSTR_MUSHROOM_SPAWN				"Mushroom spirits have turned up in %s."

//MOUNTEFFECTS
#define TSTR_DYE_FIRSTT					"Are you sure to change the effect of %s for 1500 Medals?"
#define TSTR_DYE						"Are you sure to change the effect of %s for 400 Medals?"
#define TSTR_INVALID_MOUNT				"Invalid Mount"
#define TSTR_NEEDCASH					"Not enough medals!"
#define TSTR_DELETE_EFFECT				"Are you sure to delete the effect of %s ? This cannot be undone! To dye the mount again you will have to pay again the 1500 Medals fee."
#define TSTR_DF_ERROR					"You cannot remove the color of an mount without an Color! Get a life u hackzooooor!"
#define TSTR_DF_SUCCESS					"The effect from %s was succesfully removed!"
#define TSTR_ERROR						"There occured error! Try it later again!"
#define TSRT_CF_SUCCESS					"%s was succesfully dyed!"
#define TSTR_CF_BTN						"Change Effect"
#define TSTR_AF_BTN						"Add Effect"
#define TSTR_DF_BTN						"Delete Effect"

//COLORS
#define TSTR_NEON_GREEN					" (Neon Green)"
#define TSTR_MAGIC_ICE					" (Magic Ice)"
#define TSTR_MAGIC_BLUE					" (Magic Blue)"
#define TSTR_SHINY_PINK					" (Shiny Pink)"
#define TSTR_MAGIC_PINK					" (Magic Pink)"
#define TSTR_GOLD						" (Gold)"
#define TSTR_ABYSS						" (Abyss)"

//SECURECODE SYSTEM
#define TSTR_SC_CREATE					CTChart::Localize("Create Securecode");
#define TSTR_SC_ENABLE					CTChart::Localize("Enable Securecode")
#define TSTR_SC_DISABLE					CTChart::Localize("Disable Securecode")
#define TSTR_SC_CHANGE					CTChart::Localize("Change Securecode")
#define TSTR_SC_ADVISETITLE				CTChart::Localize("Please create a Securecode.")
#define TSTR_SC_ADVISE					CTChart::Localize("Please create a Securecode for a safe management of your items.")
#define TSTR_SC_HINT					CTChart::Localize("!!! A Securecode must be 8-12 characters in length with a number and a special character.")
#define TSTR_SC_INVALID					CTChart::Localize("The entered Securecode is invalid. Please try it again.")
#define TSTR_SC_SHOWAGAIN				CTChart::Localize("Do not show again.")
#define TSTR_SC_OLDCODE					CTChart::Localize("Old Securecode")
#define TSTR_SC_ENTERCODE				CTChart::Localize("Enter Securecode")
#define TSTR_SC_CONFIRMCODE				CTChart::Localize("Confirm code")
#define TSTR_SC_CHANGECODE				CTChart::Localize("Change Securecode")

#define TSTR_SC_CODELENGTH				CTChart::Localize("Please enter 8-12 characters in length.")
#define TSTR_SC_CODETOOSHORT			CTChart::Localize("Code too short! Please enter 8-12 characters in length.")
#define TSTR_SC_CODETOOLONG				CTChart::Localize("Code too long! Please enter 8-12 characters in length.")
#define TSTR_SC_CODENOSPECIAL			CTChart::Localize("Must include numbers and special characters.")
#define TSTR_SC_CODEVALID				CTChart::Localize("This is a valid Securecode.")
#define TSTR_SC_CODENOMATCH				CTChart::Localize("This does not match the entered Securecode.")

#define TSTR_SC_UNLOCKTITLE				CTChart::Localize("Unlock Securecode")
#define TSTR_SC_UNLOCKADVISE			CTChart::Localize("Please enter your Securecode to unlock the blockade.")
#define TSTR_SC_UNLOCKBTNTXT			CTChart::Localize("Unlock")

#define TSTR_SC_UNLOCKEDTITLE			CTChart::Localize("Securecode is now unlocked.")
#define TSTR_SC_UNLOCKEDADVISE			CTChart::Localize("The Securecode has been properly unlocked.")
#define TSTR_SC_UNLOCKEDHINT			CTChart::Localize("You may access the items in the inventory and the premium storage and change the equipped items.")
#define TSTR_SC_UNLOCKEDRELOGHINT		CTChart::Localize("Log out and the Securecode system will automatically start next time you log in.")
#define TSTR_SC_UNLOCKEDCLOSE			CTChart::Localize("Close")

#define TSTR_SC_CREATESUCCESSDLGTITLE	CTChart::Localize("Create Securecode")
#define TSTR_SC_CREATESUCCESSTITLE		CTChart::Localize("Securecode created successfully.")
#define TSTR_SC_CREATESUCCESSHINT		CTChart::Localize("You have successfully created Securecode. You may use the security system now.")
#define TSTR_SC_CREATESUCCESSADVISE		CTChart::Localize("Use the Securecode system to manage items in your Inventory and Premium storage securely.")
#define TSTR_SC_CREATESUCCESSADDASK		CTChart::Localize("Do you want to enable Securecode?")
#define TSTR_SC_CREATESUCCESSENABLEBTN	CTChart::Localize("Enable")

#define TSTR_SC_DISABLEDDLGTITLE		CTChart::Localize("Disable Securecode")
#define TSTR_SC_DISABLEDTITLE			CTChart::Localize("The Securecode system has been disabled.")
#define TSTR_SC_DISABLEBTNHINT			CTChart::Localize("The Securecode system is disabled. Please manage your items with caution.")
#define TSTR_SC_DISABLEDBTN				CTChart::Localize("Close")

#define TSTR_SC_CHANGETITLE				CTChart::Localize("Change Securecode")
#define TSTR_SC_CHANGEADVISE			CTChart::Localize("Please enter the current Securecode once and the new Securecode twice to change the Securecode.")
#define TSTR_SC_CHANGENEWCODE			CTChart::Localize("Enter new code")
#define TSTR_SC_CHANGECURCODE			CTChart::Localize("Current Code")
#define TSTR_SC_CHANGEBTN				CTChart::Localize("Change")

#define TSTR_SC_DISABLEADVISE			CTChart::Localize("Please enter your Securecode.")
#define TSTR_SC_DISABLEHINT				CTChart::Localize("To disable the system, you must enter the Securecode. When disabled, you will no longer be protected by the Securecode system.")
#define TSTR_SC_DISABLEBTN				CTChart::Localize("Disable")

#define TSTR_SC_WRONGTITLE				CTChart::Localize("Securecode Error")
#define TSTR_SC_WRONGADVISE				CTChart::Localize("Entered Securecode is invalid. Please check again and re-enter your Securecode.")
#define TSTR_SC_WRONGHINT				CTChart::Localize("If you forgot your Securecode, please request a temporary Securecode on our website.")
#define TSTR_SC_WRONGFAILS				CTChart::Localize("%d time(s) failed. Please note that the system will be locked for 24 hours after 10 failed attempts.")
#define TSTR_SC_FAILEDTOOOFTEN			CTChart::Localize("You have failed to enter a correct Securecode 10 times. %d hour(s) %d minute(s) until you can try again.")

#define TSTR_SC_WRONGLEFTBTN			CTChart::Localize("Left Button")
#define TSTR_SC_WRONGRESET				CTChart::Localize("Reset")

#define TSTR_SC_ENABLESUCCESS			CTChart::Localize("The securecodesystem was enabled succesfully")
#define TSTR_SC_CODEEXIST				CTChart::Localize("The securecode does allready exist. It is enabled now!")
#define TSTR_SC_CODECHANGED				CTChart::Localize("You have successfully changed security code.")
#define TSTR_SC_NEWCODEADVIsE			CTChart::Localize("Please use your new Securecode to use the system.")
#define TSTR_SC_CHANGESUCCESS			CTChart::Localize("Successfully changed Securecode")